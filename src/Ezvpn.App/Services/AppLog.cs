using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ezvpn.App.Services;

/// <summary>
/// The app's log file, <c>%ProgramData%\ezvpn\ezvpn.log</c>: the Rust core's log
/// and the app's own connect/disconnect timings, one line each, every line
/// stamped to the millisecond when it is logged.
///
/// The core logs to stderr (<c>ezvpn_init_logging</c>), which a windowed process
/// does not have. <see cref="Start"/> points stderr at a pipe whose lines are
/// copied into the file, stamped on arrival: the core's own timestamps are to
/// the second, and it writes each record in one call, so the arrival time is
/// the time it logged. Rust looks the stderr handle up on every write, so the
/// redirect holds whenever it is set.
///
/// <see cref="Write"/> never waits: it stamps the line and queues it, and one
/// writer thread does all the file I/O. Its callers include the core's event
/// callback, which runs while the core holds a lock <c>ezvpn_stop</c> waits
/// for, and the stderr pump, which the core's own logging waits on. A queue
/// the writer cannot keep up with (a stalled disk) drops lines rather than
/// grow; <see cref="Stop"/> drains it before the process exits.
///
/// The previous run's file is kept as <c>ezvpn.log.1</c>, as is the file
/// whenever it passes <see cref="MaxBytes"/>. Logging never fails the app: a
/// file that cannot be renamed (a reader holds it without delete sharing, as
/// <c>Get-Content -Wait</c> does) keeps growing, and one that cannot be opened
/// or written drops lines; either is tried again after <see cref="RetryDelay"/>.
/// </summary>
internal static partial class AppLog
{
    private const int StdErrorHandle = -12;
    private const long MaxBytes = 10 * 1024 * 1024;
    private const int QueueCapacity = 10_000;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private static BlockingCollection<string>? _queue;
    private static Thread? _writer;
    // The pipe's write end is stderr from here on: held for the process
    // lifetime, or its finalizer would close the handle under the core.
    private static SafeFileHandle? _stderrWrite;

    // The writer thread's alone.
    private static StreamWriter? _file;
    private static DateTime _retryAt;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ezvpn",
        "ezvpn.log");

    /// <summary>
    /// Start the writer (the previous run's file kept as <c>.1</c>) and send
    /// stderr into the log. Call once, before <c>ezvpn_init_logging</c>.
    /// </summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var queue = new BlockingCollection<string>(QueueCapacity);
            _writer = new Thread(() => WriteAll(queue)) { IsBackground = true, Name = "ezvpn-log-writer" };
            _writer.Start();
            _queue = queue;
            Write("app", $"ezvpn {AppInfo.Summary} started (pid {Environment.ProcessId})");
            RedirectStderr();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ezvpn log unavailable: {ex}");
        }
    }

    /// <summary>
    /// Log one line from <paramref name="source"/>, stamped now. Thread-safe,
    /// and never waits on the file.
    /// </summary>
    public static void Write(string source, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{source}] {message}";
        try
        {
            // Full: dropped. Stopped: too late to log.
            _queue?.TryAdd(line);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Write out what is queued, waiting up to <see cref="StopTimeout"/>, and
    /// log nothing more. For the app's exit; safe to call more than once.
    /// </summary>
    public static void Stop()
    {
        if (_queue is not { IsAddingCompleted: false } queue)
        {
            return;
        }
        queue.CompleteAdding();
        _writer?.Join(StopTimeout);
    }

    private static void WriteAll(BlockingCollection<string> queue)
    {
        try
        {
            Reopen(rotate: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ezvpn log open failed: {ex}");
            _retryAt = DateTime.UtcNow + RetryDelay;
        }
        foreach (var line in queue.GetConsumingEnumerable())
        {
            try
            {
                if (DateTime.UtcNow >= _retryAt && (_file is null || _file.BaseStream.Length >= MaxBytes))
                {
                    Reopen(rotate: _file is not null);
                }
                _file?.WriteLine(line);
            }
            catch (Exception ex)
            {
                // A full disk, say: drop lines until the retry.
                Debug.WriteLine($"ezvpn log write failed: {ex}");
                _file?.Dispose();
                _file = null;
                _retryAt = DateTime.UtcNow + RetryDelay;
            }
        }
    }

    // Close the file, keep it as ezvpn.log.1 when rotate, and open ezvpn.log
    // again: anew after a rename, for append when there was none or it failed.
    private static void Reopen(bool rotate)
    {
        _file?.Dispose();
        _file = null;
        var mode = FileMode.Append;
        if (rotate && File.Exists(FilePath))
        {
            try
            {
                File.Move(FilePath, FilePath + ".1", overwrite: true);
                mode = FileMode.Create;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _retryAt = DateTime.UtcNow + RetryDelay;
            }
        }
        try
        {
            var stream = new FileStream(FilePath, mode, FileAccess.Write, FileShare.Read);
            _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"ezvpn log open failed: {ex}");
            _retryAt = DateTime.UtcNow + RetryDelay;
        }
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
