using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace MdViewer;

/// <summary>
/// Keeps one MdViewer process for the whole session. Starting the browser engine
/// costs a second or more, so opening a second document hands the path to the
/// window that is already running instead of paying that price again.
/// </summary>
internal static class SingleInstance
{
    // Both names are scoped to the logon session: "Local\" does it for the mutex, and
    // the session id does it for the pipe. Two users on one machine (a second account,
    // Remote Desktop) therefore each get their own window instead of fighting over a
    // pipe only one of them can bind.
    private static readonly string MutexName = @"Local\MdViewer.Instance";
    private static readonly string PipeName = "MdViewer.OpenDocument." + Process.GetCurrentProcess().SessionId;

    private const byte Ack = (byte)'+';
    private const int MaxPathBytes = 64 * 1024;

    private static Mutex? _mutex;
    private static NamedPipeServerStream? _server;
    private static volatile bool _stopping;

    /// <summary>Takes ownership of the session, or returns false if another window has it.</summary>
    public static bool TryClaim()
    {
        _mutex = new Mutex(true, MutexName, out var isOwner);
        if (!isOwner)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return isOwner;
    }

    /// <summary>
    /// Asks the running window to open a document. False if it could not be reached,
    /// or if it was reached but is already closing and so will not show anything.
    /// </summary>
    public static bool SendToRunningInstance(string? path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(3000);

            // This process was started by the user's click, so it holds the right to
            // bring a window to the front. The running instance has no such right, so
            // it is handed over before the path is.
            AllowSetForegroundWindow(AsfwAny);

            // Length-prefixed so the listener knows when the path is complete.
            var payload = Encoding.UTF8.GetBytes(path ?? string.Empty);
            pipe.Write(BitConverter.GetBytes(payload.Length), 0, 4);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();

            // The window answers once the document is on screen. No answer means it
            // was closing underneath us, and the document would otherwise be lost.
            var ack = new byte[1];
            var read = Task.Run(() => pipe.Read(ack, 0, 1));
            return read.Wait(5000) && read.Result == 1 && ack[0] == Ack;
        }
        catch (Exception)
        {
            // The other instance is starting up or shutting down; open our own window.
            return false;
        }
    }

    /// <summary>
    /// Listens for paths sent by later launches until <see cref="Release"/> is called.
    /// The callback runs on a background thread and returns whether the document was shown.
    /// </summary>
    public static void Listen(Func<string, bool> onDocument)
    {
        var thread = new Thread(() => ListenLoop(onDocument)) { IsBackground = true, Name = "MdViewer pipe" };
        thread.Start();
    }

    private static void ListenLoop(Func<string, bool> onDocument)
    {
        while (!_stopping)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                _server = pipe;
                pipe.WaitForConnection();

                var header = new byte[4];
                pipe.ReadExactly(header, 0, 4);
                var length = BitConverter.ToInt32(header, 0);
                if (length < 0 || length > MaxPathBytes) continue; // not one of ours

                var payload = new byte[length];
                pipe.ReadExactly(payload, 0, length);

                var path = Encoding.UTF8.GetString(payload).Trim();
                var shown = !_stopping && onDocument(path);
                if (shown)
                {
                    pipe.WriteByte(Ack);
                    pipe.Flush();
                }
            }
            catch (Exception)
            {
                // A broken connection must not take the listener down.
                if (!_stopping) Thread.Sleep(200);
            }
            finally
            {
                _server = null;
            }
        }
    }

    /// <summary>
    /// Gives the session up. Called as the window starts closing, so that a document
    /// opened from then on gets a fresh window instead of being handed to a dying one.
    /// </summary>
    public static void Release()
    {
        _stopping = true;
        try
        {
            _server?.Dispose(); // unblocks WaitForConnection
        }
        catch (Exception)
        {
            // Already gone.
        }

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned on this thread; disposing is enough.
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
