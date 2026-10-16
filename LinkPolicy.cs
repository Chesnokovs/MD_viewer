namespace MdViewer;

/// <summary>
/// The rules for what a document may reach. A Markdown file may contain arbitrary
/// links and HTML, so everything it points at is treated as untrusted input. Kept
/// free of any UI so the rules can be unit-tested.
/// </summary>
internal static class LinkPolicy
{
    /// <summary>Made-up host from which the current document's own files are served.</summary>
    public const string DocumentHost = "doc.mdviewer.local";

    /// <summary>Files that open inside the viewer instead of being handed to the shell.</summary>
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd", ".mdtext", ".txt",
    };

    /// <summary>
    /// Extensions the shell would execute rather than display. A link inside a document
    /// must never start a program, so these are revealed in Explorer instead of opened.
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msp", ".msc", ".cpl", ".reg",
        ".lnk", ".url", ".jar", ".appref-ms", ".application", ".gadget", ".inf", ".scf",
        ".sct", ".chm", ".dll", ".ocx", ".py", ".pyw", ".sh",
    };

    /// <summary>
    /// The only schemes handed to Windows. A document could otherwise ask for
    /// javascript:, ms-settings:, or a file: URL on a UNC share, which would start a
    /// program or leak credentials to the share's host.
    /// </summary>
    private static readonly HashSet<string> SafeExternalSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto",
    };

    public static bool IsMarkdown(string path) => MarkdownExtensions.Contains(Path.GetExtension(path));

    public static bool IsExecutable(string path) => ExecutableExtensions.Contains(Path.GetExtension(path));

    /// <summary>Parses an external link and returns it only if Windows may be asked to open it.</summary>
    public static Uri? SafeExternalUri(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return null;
        return SafeExternalSchemes.Contains(uri.Scheme) ? uri : null;
    }

    /// <summary>
    /// Turns a link clicked in the document into a path, or null if the link may not be
    /// followed at all. "..\.." is ordinary in a documentation tree, so a link may leave
    /// the document's folder, but it stays on the same drive or network share.
    /// </summary>
    public static string? ResolveLinkTarget(string href, string documentFolder)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(href.Split('#')[0])
                .Replace('/', Path.DirectorySeparatorChar);
            if (decoded.Length == 0) return null;

            // Relative links only. A document naming an absolute path or a UNC share is
            // either broken or reaching for somewhere it has no business reaching.
            if (Path.IsPathRooted(decoded)) return null;

            var root = Path.GetFullPath(documentFolder);
            var full = Path.GetFullPath(Path.Combine(root, decoded));

            return SameVolume(full, root) ? full : null;
        }
        catch (Exception)
        {
            return null; // a malformed link is not worth reporting
        }
    }

    /// <summary>
    /// The URL the page resolves a document's relative image paths against. The folder
    /// is spelled out in the URL path, so "../images/logo.png" in the document becomes
    /// a request for the real sibling folder rather than being collapsed by the browser
    /// at the host's root.
    /// </summary>
    public static string DocumentBaseUrl(string documentFolder)
    {
        var full = Path.GetFullPath(documentFolder);
        var builder = new System.Text.StringBuilder("https://" + DocumentHost + "/");

        string rest;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            builder.Append("unc/");
            rest = full[2..];
        }
        else
        {
            rest = full;
        }

        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            builder.Append(Uri.EscapeDataString(segment)).Append('/');

        return builder.ToString();
    }

    /// <summary>
    /// Maps a request for one of the document's files back onto a path, or null if the
    /// request may not be served. Embedded files load without a click, which is why they
    /// are still confined to the document's own drive or share; existence is left to the
    /// caller so the rule itself needs no files on disk.
    /// </summary>
    public static string? ResolveDocumentFile(string requestUri, string documentFolder)
    {
        try
        {
            var uri = new Uri(requestUri);
            if (!string.Equals(uri.Host, DocumentHost, StringComparison.OrdinalIgnoreCase)) return null;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
            if (segments.Length == 0) return null;

            // A segment that still contains a separator after unescaping ("..%5C") would
            // let GetFullPath climb in a way the split above never saw.
            if (segments.Any(s => s.Contains('\\') || s.Contains('/'))) return null;

            // The path must name its volume outright: "unc/server/share/..." or "C:/...".
            // Anything else ("logo.png" at the host's root, or "C:" alone) would be
            // completed from the process's current directory, which is nobody's business.
            string candidate;
            if (string.Equals(segments[0], "unc", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length < 4) return null; // server, share and at least a file
                candidate = @"\\" + string.Join('\\', segments[1..]);
            }
            else
            {
                if (segments.Length < 2 || !IsDriveSegment(segments[0])) return null;
                candidate = segments[0] + '\\' + string.Join('\\', segments[1..]);
            }

            var full = Path.GetFullPath(candidate);

            var root = Path.GetFullPath(documentFolder);
            return SameVolume(full, root) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsDriveSegment(string segment) =>
        segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':';

    /// <summary>
    /// Enough "\.." climbs out of any folder, so the boundary is the volume: a local
    /// document cannot reach a network share, where merely opening a file hands
    /// credentials to whoever runs it, nor hop to another drive.
    /// </summary>
    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}
