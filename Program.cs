namespace MdViewer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Per-monitor DPI is configured in the project file (ApplicationHighDpiMode) and
        // applied here, before any window exists.
        ApplicationConfiguration.Initialize();

        // A stray exception should say what went wrong, not drop the standard .NET
        // crash dialog on a user who only wanted to read a document. One on the UI
        // thread is survivable, so the window stays open; one anywhere else is not.
        Application.ThreadException += (_, e) => Report(e.Exception,
            "MdViewer hit an unexpected error. The window is still open, but reopening the "
            + "document is the safer move.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception,
            "MdViewer hit an unexpected error and has to close.");

        string? path = args.Length > 0 ? args[0] : null;
        var settings = Settings.Load();

        // One window per session by default: starting the browser engine again for
        // every document is what makes opening a file feel slow.
        var owner = true;
        if (settings.SingleWindow)
        {
            owner = SingleInstance.TryClaim();
            if (!owner && SingleInstance.SendToRunningInstance(path)) return;
        }

        var form = new MainForm(path, settings);

        // Only the process that holds the session serves the pipe. A second listener
        // could never bind it and would spin instead.
        if (settings.SingleWindow && owner)
        {
            SingleInstance.Listen(form.OpenFromOtherInstance);
            // Hand the session back as soon as the window starts closing, not when the
            // process finally exits: a launch in between gets its own window.
            form.FormClosing += (_, _) => SingleInstance.Release();
        }

        Application.Run(form);
        SingleInstance.Release();
    }

    private static void Report(Exception? exception, string headline)
    {
        MessageBox.Show(headline + "\n\n" + (exception?.Message ?? "Unknown error"),
            "MdViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
