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
/// grow; <see cref="Stop"/> drains stderr into it, then it into the file,
/// before the process exits.
///
/// The previous run's file is kept as <c>ezvpn.log.1</c>, as is the file
/// whenever it passes <see cref="MaxBytes"/>. Logging never fails the app: a
/// file that cannot be renamed (a reader holds it without delete sharing, as
/// <c>Get-Content -Wait</c> does) keeps growing until a retry renames it, and
/// one that cannot be opened or written drops lines until a retry reopens it;
/// retries come every <see cref="RetryDelay"/>.
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

    // Written through stderr by Stop: once the pump reads it back, every line
    // the core wrote before it is queued (a pipe keeps its order).
    private static readonly string DrainMarker = $"ezvpn-log-drained-{Guid.NewGuid():N}";
    private static readonly ManualResetEventSlim StderrDrained = new();

    // The writer thread's alone.
    private static StreamWriter? _file;
    private static DateTime _retryAt;
    // A rename that failed, tried again at the retry whatever the file's size.
    private static bool _rotatePending;

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
    /// Queue what the core has written to stderr so far, write out the queue,
    /// and log nothing more, all within <see cref="StopTimeout"/>. For the
    /// app's exit, after the native teardown; safe to call more than once.
    /// </summary>
    public static void Stop()
    {
        if (_queue is not { IsAddingCompleted: false } queue)
        {
            return;
        }
        var clock = Stopwatch.StartNew();
        DrainStderr(StopTimeout);
        queue.CompleteAdding();
        var left = StopTimeout - clock.Elapsed;
        _writer?.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
    }

    // Wait, up to timeout, until the pump has read back a marker written
    // after everything the core has written. The write runs on its own task:
    // one into a pipe that nothing reads any more would not return.
    private static void DrainStderr(TimeSpan timeout)
    {
        if (_stderrWrite is not { } write)
        {
            return;
        }
        var marker = Encoding.UTF8.GetBytes(DrainMarker + "\n");
        _ = Task.Run(() => WriteFile(write, marker, (uint)marker.Length, out _, IntPtr.Zero));
        StderrDrained.Wait(timeout);
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
                var rotate = _rotatePending || (_file is not null && _file.BaseStream.Length >= MaxBytes);
                if ((_file is null || rotate) && DateTime.UtcNow >= _retryAt)
                {
                    Reopen(rotate);
                }
                _file?.WriteLine(line);
            }
            catch (Exception ex)
            {
                // A full disk, say: drop lines until the retry.
                Debug.WriteLine($"ezvpn log write failed: {ex}");
                _retryAt = DateTime.UtcNow + RetryDelay;
                CloseFile();
            }
        }
    }

    // Close the file, keep it as ezvpn.log.1 when rotate, and open ezvpn.log
    // again: anew after a rename, for append when there was none or it failed.
    private static void Reopen(bool rotate)
    {
        CloseFile();
        var mode = FileMode.Append;
        if (rotate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }
                mode = FileMode.Create;
                _rotatePending = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _rotatePending = true;
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

    // Let go of the file before closing it: a close whose flush retries a
    // write that just failed can throw again, which must not end the writer
    // thread (and with it the process).
    private static void CloseFile()
    {
        var file = _file;
        _file = null;
        try
        {
            file?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ezvpn log close failed: {ex}");
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
                // The marker ends the line it is read on: it follows whatever
                // the core left unterminated.
                if (line.EndsWith(DrainMarker, StringComparison.Ordinal))
                {
                    if (line.Length > DrainMarker.Length)
                    {
                        Write("core", line[..^DrainMarker.Length]);
                    }
                    StderrDrained.Set();
                    continue;
                }
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(
        SafeFileHandle hFile, ReadOnlySpan<byte> lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);
}
