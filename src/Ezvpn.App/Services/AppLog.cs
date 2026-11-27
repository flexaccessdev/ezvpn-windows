using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ezvpn.App.Services;

/// <summary>
/// The app's log file, <c>%ProgramData%\ezvpn\ezvpn.log</c>: the Rust core's log
/// and the app's own connect/disconnect timings, one line each, every line
/// stamped to the millisecond as it is written.
///
/// The core logs to stderr (<c>ezvpn_init_logging</c>), which a windowed process
/// does not have. <see cref="Start"/> points stderr at a pipe whose lines are
/// copied into the file, stamped on arrival: the core's own timestamps are to
/// the second, and it writes each record in one call, so the arrival time is
/// the time it logged. Rust looks the stderr handle up on every write, so the
/// redirect holds whenever it is set.
///
/// The previous run's file is kept as <c>ezvpn.log.1</c>, as is the file
/// whenever it passes <see cref="MaxBytes"/>. Logging never fails the app: a
/// file that cannot be opened leaves the app running without it.
/// </summary>
internal static partial class AppLog
{
    private const int StdErrorHandle = -12;
    private const long MaxBytes = 10 * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;
    // The pipe's write end is stderr from here on: held for the process
    // lifetime, or its finalizer would close the handle under the core.
    private static SafeFileHandle? _stderrWrite;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ezvpn",
        "ezvpn.log");

    /// <summary>
    /// Open the log (the previous one kept as <c>.1</c>) and send stderr into
    /// it. Call once, before <c>ezvpn_init_logging</c>.
    /// </summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (Gate)
            {
                Open();
            }
            Write("app", $"ezvpn {AppInfo.Summary} started (pid {Environment.ProcessId})");
            RedirectStderr();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ezvpn log unavailable: {ex}");
        }
    }

    /// <summary>Append one line from <paramref name="source"/> (thread-safe).</summary>
    public static void Write(string source, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{source}] {message}";
        lock (Gate)
        {
            if (_writer is null)
            {
                return;
            }
            try
            {
                if (_writer.BaseStream.Length >= MaxBytes)
                {
                    _writer.Dispose();
                    Open();
                }
                _writer.WriteLine(line);
            }
            catch (Exception ex)
            {
                // A full disk or a file removed under us: stop logging, keep running.
                Debug.WriteLine($"ezvpn log write failed: {ex}");
                _writer = null;
            }
        }
    }

    // Under Gate. Keeps what is there as ezvpn.log.1 and starts a new file.
    private static void Open()
    {
        if (File.Exists(FilePath))
        {
            File.Move(FilePath, FilePath + ".1", overwrite: true);
        }
        var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static void RedirectStderr()
    {
        if (!CreatePipe(out var read, out var write, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
        }
        if (!SetStdHandle(StdErrorHandle, write.DangerousGetHandle()))
        {
            var error = Marshal.GetLastWin32Error();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error, "SetStdHandle failed");
        }
        _stderrWrite = write;
        new Thread(() => Pump(read)) { IsBackground = true, Name = "ezvpn-log" }.Start();
    }

    // Copy stderr into the log a line at a time. It runs as long as the
    // process: the write end is never closed.
    private static void Pump(SafeFileHandle read)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(read, FileAccess.Read, 0), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                Write("core", line);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ezvpn log pump stopped: {ex}");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(
        out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int nStdHandle, IntPtr hHandle);
}
