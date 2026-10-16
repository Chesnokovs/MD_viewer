using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace MdViewer;

internal sealed class MainForm : Form
{
    private const string AppHost = "assets.mdviewer.local";
    private const string AppUrl = "https://" + AppHost + "/viewer.html";

    private const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>
    /// A Markdown file may contain arbitrary HTML and Markdig passes it through, so the
    /// page has to be treated as untrusted content. Without 'unsafe-inline' in script-src
    /// an inline handler such as an img onerror attribute cannot run, which is what stops
    /// a document from driving the host through postMessage. Remote images stay allowed
    /// because README badges are everywhere; nothing else may reach the network.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' https: data: blob:; " +   // https: also covers the document host
        "media-src 'self' https: data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'none'; " +
        "frame-src 'none'; " +
        "object-src 'none'; " +
        "base-uri 'none'; " +
        "form-action 'none'";

    private const int ReloadRetryLimit = 20;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Settings _settings;
    private readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = 250 };

    private readonly Dictionary<string, byte[]> _webAssets = LoadWebAssets();
    private readonly Task<CoreWebView2Environment> _environmentTask;

    private CoreWebView2Environment? _environment;
    private FileSystemWatcher? _watcher;
    private string? _watchedPath;
    private string? _currentPath;
    private string? _documentFolder;
    private bool _uiReady;
    private bool _closing;
    private string? _pendingPath;
    private int _reloadAttempts;

    public MainForm(string? initialPath, Settings settings)
    {
        _settings = settings;

        // Creating the browser environment is the slowest part of starting up, so it
        // runs while the window itself is still being built.
        _environmentTask = CreateEnvironmentAsync();

        Text = "MdViewer";
        MinimumSize = new Size(520, 380);
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;
        Icon = TryLoadAppIcon();
        ApplySavedBounds();

        ApplyHostTheme(IsDarkTheme(_settings.Theme));
        _pendingPath = initialPath;

        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            if (_currentPath is not null) LoadFile(_currentPath, isReload: true);
        };

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) LoadFile(files[0]);
        };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        FormClosing += (_, _) =>
        {
            _closing = true;
            SaveBounds();
        };
        FormClosed += (_, _) =>
        {
            // Release the browser profile promptly; a lingering engine makes the
            // next launch wait for the lock.
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _reloadTimer.Dispose();
            _watcher?.Dispose();
            _web.Dispose();
        };

        Controls.Add(_web);
        Load += async (_, _) => await InitialiseWebViewAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme(IsDarkTheme(_settings.Theme));
    }

    // ---------- Theme ----------

    private void ApplyHostTheme(bool dark)
    {
        BackColor = dark ? Color.FromArgb(13, 17, 23) : Color.White;
        _web.DefaultBackgroundColor = BackColor;
        ApplyTitleBarTheme(dark);
    }

    /// <summary>Follows Windows when the theme is left on "system" and the user changes it.</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _settings.Theme != "system") return;
        TryBeginInvoke(() => ApplyHostTheme(IsSystemDark()));
    }

    /// <summary>Matches the Windows title bar to the viewer's own theme.</summary>
    private void ApplyTitleBarTheme(bool dark)
    {
        if (!IsHandleCreated) return;
        var value = dark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows builds simply keep the light title bar.
        }
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- WebView2 setup ----------

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        // Background work the viewer has no use for, switched off to start faster.
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ',
                "--disable-background-networking",
                "--disable-component-update",
                "--disable-sync",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-features=Translate,OptimizationGuideModelDownloading,MediaRouter"),
        };
        return CoreWebView2Environment.CreateAsync(null, AppPaths.BrowserFolder, options);
    }

    private async Task InitialiseWebViewAsync()
    {
        try
        {
            _environment = await _environmentTask;
            await _web.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Could not start the WebView2 runtime.\n\n" + ex.Message +
                "\n\nInstall the Microsoft Edge WebView2 Runtime and try again:\n" + WebView2DownloadUrl,
                "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("MDVIEWER_DEV") == "1";
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        _web.AllowExternalDrop = false; // dropped files are handled by the form

        // The interface is served straight out of the executable, and the files the
        // document references come from the document's own folder. Nothing is written
        // to disk, so there is no folder mapping that could go stale or be blocked.
        core.AddWebResourceRequestedFilter("https://" + AppHost + "/*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("https://" + LinkPolicy.DocumentHost + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            // The shell page is the only thing allowed to load in the window.
            if (e.Uri.StartsWith(AppUrl, StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;

            // A link the user clicked goes to the browser. A navigation the document
            // started by itself, say through a <meta http-equiv="refresh"> smuggled in
            // as raw HTML, goes nowhere: nothing leaves the viewer without a click.
            if (e.IsUserInitiated) OpenExternal(e.Uri);
        };

        _web.Source = new Uri(AppUrl);
    }

    /// <summary>Reads the embedded HTML/CSS/JS of the viewer interface into memory.</summary>
    private static Dictionary<string, byte[]> LoadWebAssets()
    {
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();
        const string prefix = "MdViewer.assets.";

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            using var source = assembly.GetManifestResourceStream(name);
            if (source is null) continue;
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            assets[name[prefix.Length..]] = buffer.ToArray();
        }
        return assets;
    }

    /// <summary>Reports a page that failed to load instead of leaving a browser error behind.</summary>
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) return;

        // Links and redirects are cancelled on purpose in NavigationStarting and handed to
        // the shell instead; that is not a failure worth a dialog.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
        if (!string.Equals(_web.Source?.ToString(), AppUrl, StringComparison.OrdinalIgnoreCase)) return;

        MessageBox.Show(this,
            "The viewer interface could not be loaded (" + e.WebErrorStatus + ").",
            "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// A crashed renderer would otherwise leave a blank window. Reloading the shell page
    /// brings the document back through the ready message; only a dead browser process
    /// cannot be recovered from in place.
    /// </summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            MessageBox.Show(this,
                "The browser engine stopped unexpectedly. Please reopen the document.",
                "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        _uiReady = false;
        try
        {
            _web.CoreWebView2?.Reload();
        }
        catch (Exception)
        {
            // The control itself may be gone; the user still has the dialog above.
        }
    }

    // ---------- Messages from the page ----------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the shell page may talk to the host.
        if (!e.Source.StartsWith(AppUrl, StringComparison.OrdinalIgnoreCase)) return;

        JsonElement message;
        try
        {
            message = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "ready":
                _uiReady = true;
                Post(new
                {
                    type = "init",
                    theme = _settings.Theme,
                    tocVisible = _settings.TocVisible,
                    version = Application.ProductVersion.Split('+')[0],
                });

                // Falling back to the current path also covers a reload of the shell page:
                // the open document comes back instead of the window going empty.
                var resume = _pendingPath ?? _currentPath;
                _pendingPath = null;
                if (resume is not null) LoadFile(resume);
                break;

            case "open":
                ShowOpenDialog();
                break;

            case "openPath":
                if (message.TryGetProperty("path", out var p) && p.GetString() is { } raw) OpenRelative(raw);
                break;

            case "external":
                if (message.TryGetProperty("url", out var u) && u.GetString() is { } url) OpenExternal(url);
                break;

            case "theme":
                if (message.TryGetProperty("value", out var v) && v.GetString() is { } theme)
                {
                    _settings.Theme = theme;
                    _settings.Save();
                    ApplyHostTheme(IsDarkTheme(theme));
                }
                break;

            case "toc":
                if (message.TryGetProperty("value", out var tv))
                {
                    _settings.TocVisible = tv.ValueKind == JsonValueKind.True;
                    _settings.Save();
                }
                break;
        }
    }

    private void Post(object payload)
    {
        if (_web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    // ---------- Loading documents ----------

    /// <summary>
    /// Opens a document handed over by a second launch. Called from a background thread,
    /// and returns false if the window is closing and so cannot show it; the caller then
    /// lets the other launch open a window of its own.
    /// </summary>
    public bool OpenFromOtherInstance(string path)
    {
        try
        {
            if (_closing || IsDisposed || !IsHandleCreated) return false;
            return (bool)Invoke(() =>
            {
                if (_closing) return false;
                LoadFile(path);
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                // The launching process granted us the foreground right over the pipe.
                SetForegroundWindow(Handle);
                Activate();
                return true;
            });
        }
        catch (Exception)
        {
            // The window went away between the check and the call.
            return false;
        }
    }

    /// <summary>Marshals to the UI thread, tolerating a window that is closing right now.</summary>
    private void TryBeginInvoke(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(action);
        }
        catch (Exception)
        {
            // The window went away between the check and the call; nothing left to update.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void ShowOpenDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open Markdown file",
            Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.mdtext|Text files|*.txt|All files|*.*",
            InitialDirectory = _currentPath is null ? null : Path.GetDirectoryName(_currentPath),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadFile(dialog.FileName);
    }

    /// <summary>Follows a link from the document, within the limits set by <see cref="LinkPolicy"/>.</summary>
    private void OpenRelative(string href)
    {
        var full = LinkPolicy.ResolveLinkTarget(href, _documentFolder ?? Environment.CurrentDirectory);
        if (full is null) return;

        if (File.Exists(full) && LinkPolicy.IsMarkdown(full))
            LoadFile(full);
        else if (LinkPolicy.IsExecutable(full))
            RevealInExplorer(full);
        else if (File.Exists(full) || Directory.Exists(full))
            Shell(full);
    }

    private void LoadFile(string path, bool isReload = false)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return;
        }

        // Nothing can be shown before the page reports in, and parsing now would be
        // thrown away; the path is picked up again from the ready message.
        if (!_uiReady)
        {
            _pendingPath = full;
            return;
        }

        if (!File.Exists(full))
        {
            Post(new { type = "error", message = "File not found:\n" + full });
            return;
        }

        string html;
        try
        {
            html = MarkdownRenderer.ToHtml(MarkdownRenderer.ReadFile(full));
        }
        catch (IOException) when (isReload)
        {
            // The editor may still be writing the file; try again shortly.
            RetryReload();
            return;
        }
        catch (Exception ex)
        {
            Post(new { type = "error", message = "Could not read the file:\n" + ex.Message });
            return;
        }

        _reloadAttempts = 0;
        var sameDocument = isReload && string.Equals(_currentPath, full, StringComparison.OrdinalIgnoreCase);
        _currentPath = full;
        _documentFolder = Path.GetDirectoryName(full);
        WatchFile(full);

        Text = Path.GetFileName(full) + " — MdViewer";
        Post(new
        {
            type = "document",
            html,
            name = Path.GetFileName(full),
            path = full,
            baseUrl = LinkPolicy.DocumentBaseUrl(_documentFolder ?? Environment.CurrentDirectory),
            keepScroll = sameDocument,
        });
    }

    /// <summary>Waits for a writer to finish, but not forever.</summary>
    private void RetryReload()
    {
        if (++_reloadAttempts > ReloadRetryLimit)
        {
            _reloadAttempts = 0;
            Post(new { type = "error", message = "The file stayed locked by another program." });
            return;
        }
        _reloadTimer.Start();
    }

    /// <summary>Answers every request the page makes: the interface, then the document's files.</summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_environment is null) return;

        if (e.Request.Uri.StartsWith("https://" + AppHost + "/", StringComparison.OrdinalIgnoreCase))
        {
            ServeWebAsset(e);
            return;
        }

        var file = _documentFolder is null ? null : LinkPolicy.ResolveDocumentFile(e.Request.Uri, _documentFolder);
        if (file is null || !File.Exists(file))
        {
            e.Response = _environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            return;
        }

        try
        {
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            e.Response = _environment.CreateWebResourceResponse(stream, 200, "OK",
                "Content-Type: " + LinkPolicy.ContentTypeFor(file) +
                "\r\nX-Content-Type-Options: nosniff");
        }
        catch (Exception)
        {
            e.Response = _environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
        }
    }

    /// <summary>Serves one file of the viewer interface from the executable's resources.</summary>
    private void ServeWebAsset(CoreWebView2WebResourceRequestedEventArgs e)
    {
        var name = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');

        if (_environment is null || !_webAssets.TryGetValue(name, out var content))
        {
            e.Response = _environment?.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            return;
        }

        var headers = "Content-Type: " + LinkPolicy.ContentTypeFor(name) +
                      "\r\nX-Content-Type-Options: nosniff" +
                      "\r\nCache-Control: no-cache";

        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            headers += "\r\nContent-Security-Policy: " + ContentSecurityPolicy;
            content = WithResolvedTheme(content);
        }

        e.Response = _environment.CreateWebResourceResponse(new MemoryStream(content), 200, "OK", headers);
    }

    /// <summary>
    /// Stamps the saved theme into the shell page. The page would otherwise paint light and
    /// repaint dark a frame later, which reads as a white flash on a dark desktop.
    /// </summary>
    private byte[] WithResolvedTheme(byte[] html)
    {
        var text = Encoding.UTF8.GetString(html);
        var resolved = IsDarkTheme(_settings.Theme) ? "dark" : "light";
        return Encoding.UTF8.GetBytes(
            text.Replace("data-theme=\"light\"", "data-theme=\"" + resolved + "\""));
    }

    private void WatchFile(string path)
    {
        // Live reload keeps saving the same file; rebuilding the watcher each time is waste.
        if (_watcher is not null && string.Equals(_watchedPath, path, StringComparison.OrdinalIgnoreCase)) return;

        _watcher?.Dispose();
        _watcher = null;
        _watchedPath = null;

        try
        {
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                               | NotifyFilters.CreationTime,
            };
            FileSystemEventHandler onChange = (_, _) => TryBeginInvoke(RestartReloadTimer);
            watcher.Changed += onChange;
            watcher.Created += onChange;
            watcher.Renamed += (_, _) => TryBeginInvoke(RestartReloadTimer);
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _watchedPath = path;
        }
        catch (Exception)
        {
            _watcher = null; // watching is a convenience, not a requirement
        }
    }

    private void RestartReloadTimer()
    {
        _reloadAttempts = 0;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    // ---------- Helpers ----------

    /// <summary>Hands a web link to Windows, if <see cref="LinkPolicy"/> allows the scheme.</summary>
    private static void OpenExternal(string target)
    {
        var uri = LinkPolicy.SafeExternalUri(target);
        if (uri is not null) Shell(uri.AbsoluteUri);
    }

    /// <summary>Shows a file in Explorer instead of running it.</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }
        catch (Exception)
        {
            // Explorer is missing or refused; there is nothing else to try.
        }
    }

    private static void Shell(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Nothing sensible to do if the shell refuses to open the target.
        }
    }

    private static bool IsDarkTheme(string theme) => theme switch
    {
        "dark" => true,
        "light" => false,
        _ => IsSystemDark(),
    };

    private static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Loads the window icon from the embedded .ico so every size stays crisp.</summary>
    private Icon? TryLoadAppIcon()
    {
        try
        {
            if (_webAssets.TryGetValue("app.ico", out var data))
            {
                using var stream = new MemoryStream(data);
                return new Icon(stream);
            }
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ApplySavedBounds()
    {
        var width = _settings.Width > 0 ? _settings.Width : 1040;
        var height = _settings.Height > 0 ? _settings.Height : 760;
        Size = new Size(width, height);

        if (_settings.X != int.MinValue && _settings.Y != int.MinValue)
        {
            var candidate = new Rectangle(_settings.X, _settings.Y, width, height);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(candidate)))
            {
                Location = new Point(_settings.X, _settings.Y);
                if (_settings.Maximized) WindowState = FormWindowState.Maximized;
                return;
            }
        }

        StartPosition = FormStartPosition.CenterScreen;
        if (_settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveBounds()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        _settings.X = bounds.X;
        _settings.Y = bounds.Y;
        _settings.Width = bounds.Width;
        _settings.Height = bounds.Height;
        _settings.Save();
    }
}
