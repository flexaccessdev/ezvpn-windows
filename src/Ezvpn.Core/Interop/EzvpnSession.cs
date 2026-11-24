using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ezvpn.Core.Interop;

/// <summary>Thrown when <c>ezvpn_start</c> fails during setup.</summary>
public sealed class EzvpnException : Exception
{
    public EzvpnException(string message) : base(message)
    {
    }
}

/// <summary>A state change reported by the running tunnel (<c>ezvpn_start</c>'s <c>on_event</c>).</summary>
public enum EzvpnEventKind
{
    /// <summary>An attempt failed or the session was lost; the core tries again.</summary>
    Reconnecting,

    /// <summary>The tunnel is up (first connection or a reconnect).</summary>
    Connected,

    /// <summary>Final: the core's run loop ended and will not try again.</summary>
    Ended,
}

/// <param name="Kind">What happened.</param>
/// <param name="Message">The error (<see cref="EzvpnEventKind.Reconnecting"/>), the
/// reason (<see cref="EzvpnEventKind.Ended"/>, null for an end without an error),
/// or null.</param>
public sealed record EzvpnEvent(EzvpnEventKind Kind, string? Message);

/// <summary>
/// Managed wrapper over the <c>ezvpn.dll</c> C FFI. A live session owns the
/// tunnel; <see cref="Dispose"/> stops it (and waits for teardown).
/// </summary>
public sealed class EzvpnSession : IDisposable
{
    private const int MaxStatusBytes = 1 << 20; // 1 MiB ceiling for a snapshot.

    private readonly EzvpnSafeHandle _handle;

    private EzvpnSession(EzvpnSafeHandle handle)
    {
        _handle = handle;
    }

    /// <summary>Initialize the Rust logger (stderr). Idempotent.</summary>
    public static void InitLogging() => EzvpnNative.InitLogging();

    /// <summary>
    /// Verify the native runtime dependencies are present beside the app, so a
    /// missing DLL fails fast at startup with a clear, actionable message instead
    /// of a cryptic load crash (ezvpn.dll) or a deferred failure at connect time
    /// (wintun.dll). Call this before the first FFI call. Throws
    /// <see cref="EzvpnException"/> listing whatever is missing.
    /// </summary>
    public static void EnsureNativeDependencies()
    {
        var dir = AppContext.BaseDirectory;
        var problems = new List<string>();

        if (!File.Exists(Path.Combine(dir, "ezvpn.dll")))
        {
            problems.Add(
                "ezvpn.dll is missing. Build it from the sibling core repo " +
                "(..\\ezvpn\\build-windows.ps1) and either copy it into native\\ or " +
                "build with EZVPN_LOCAL_DLL=1.");
        }

        if (!File.Exists(Path.Combine(dir, "wintun.dll")))
        {
            problems.Add(
                "wintun.dll is missing. Download it from https://www.wintun.net/ " +
                "(wintun\\bin\\amd64\\wintun.dll) and copy it into native\\. It must " +
                "sit next to ezvpn.dll to create the tunnel adapter.");
        }

        if (problems.Count > 0)
        {
            throw new EzvpnException(
                $"Required native libraries are missing from:\n{dir}\n\n" +
                string.Join("\n\n", problems));
        }
    }

    /// <summary>
    /// Start the client from an <c>ezvpn_start</c> config JSON string (build it
    /// with <see cref="EzvpnConfig.Build"/>). Returns once *started*; the
    /// tunnel's state changes then arrive through <paramref name="onEvent"/>,
    /// and <see cref="TryGetStatus"/> gives the live details. Throws
    /// <see cref="EzvpnException"/> on a setup failure.
    /// </summary>
    /// <param name="onEvent">Called on a library thread — possibly before this
    /// method returns — until <see cref="Dispose"/>, which waits out a call in
    /// progress. It must return quickly and must never wait on a thread that
    /// may be disposing this session (post to the UI thread asynchronously).
    /// Exceptions it throws are swallowed.</param>
    public static unsafe EzvpnSession Start(string configJson, Action<EzvpnEvent> onEvent)
    {
        var err = new byte[4096];
        var context = GCHandle.Alloc(onEvent);
        var ptr = EzvpnNative.Start(configJson, &OnNativeEvent, GCHandle.ToIntPtr(context), err, (nuint)err.Length);
        if (ptr == IntPtr.Zero)
        {
            // No handle, so no event can follow: free the context now.
            context.Free();
            var msg = EzvpnNative.ReadCString(err);
            throw new EzvpnException(msg.Length > 0 ? msg : "ezvpn_start failed");
        }
        return new EzvpnSession(EzvpnSafeHandle.FromPtr(ptr, context));
    }

    /// <summary>The <c>on_event</c> trampoline: decode and hand to the session's handler.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeEvent(IntPtr ctx, int code, IntPtr message)
    {
        // An exception must never unwind into the native caller.
        try
        {
            var kind = code switch
            {
                EzvpnNative.EventReconnecting => EzvpnEventKind.Reconnecting,
                EzvpnNative.EventConnected => EzvpnEventKind.Connected,
                EzvpnNative.EventEnded => EzvpnEventKind.Ended,
                _ => (EzvpnEventKind?)null,
            };
            if (kind is null || GCHandle.FromIntPtr(ctx).Target is not Action<EzvpnEvent> handler)
            {
                return;
            }
            handler(new EzvpnEvent(kind.Value, Marshal.PtrToStringUTF8(message)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ezvpn event handler failed: {ex}");
        }
    }

    /// <summary>
    /// Snapshot the live status JSON, or null if unavailable (disposed / null
    /// handle). Grows the buffer automatically if the snapshot is large.
    /// </summary>
    public string? TryGetStatusJson() => TryCallJson(EzvpnNative.Status);

    /// <summary>
    /// Snapshot the connection-path JSON (<c>ezvpn_conn_path</c>), or null if
    /// unavailable. Blocks on the custom relays' health requests, so call it off
    /// the UI thread and only on demand.
    /// </summary>
    public string? TryGetConnPathJson() => TryCallJson(EzvpnNative.ConnPath);

    /// <summary>Snapshot the parsed connection paths, or null if unavailable.</summary>
    public ConnPathSnapshot? TryGetConnPath() => ConnPathSnapshot.Parse(TryGetConnPathJson());

    /// <summary>
    /// Run one of the handle's JSON-snapshot calls (<c>1</c> full, <c>0</c>
    /// truncated, <c>-1</c> null handle), growing the buffer until it fits.
    /// </summary>
    private string? TryCallJson(Func<IntPtr, byte[], nuint, int> call)
    {
        if (_handle.IsInvalid || _handle.IsClosed)
        {
            return null;
        }

        // Keep the handle alive across the calls; the source-generated P/Invoke
        // marshals a raw IntPtr, so ref-count manually to prevent a concurrent
        // Dispose from freeing it mid-call.
        var addedRef = false;
        try
        {
            _handle.DangerousAddRef(ref addedRef);
            var ptr = _handle.DangerousGetHandle();

            var size = 8192;
            while (true)
            {
                var buf = new byte[size];
                var rc = call(ptr, buf, (nuint)buf.Length);
                if (rc < 0)
                {
                    return null;
                }
                if (rc == 0)
                {
                    if (size >= MaxStatusBytes)
                    {
                        // rc == 0 means the response was truncated to fit the
                        // buffer. At the ceiling we can't grow further, so the
                        // content is incomplete — returning it would hand back
                        // unparseable/partial JSON. Signal "unavailable" instead.
                        return null;
                    }
                    size = Math.Min(size * 2, MaxStatusBytes);
                    continue;
                }
                return EzvpnNative.ReadCString(buf);
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (addedRef)
            {
                _handle.DangerousRelease();
            }
        }
    }

    /// <summary>Snapshot the parsed status, or null if unavailable.</summary>
    public ClientStatus? TryGetStatus() => ClientStatus.Parse(TryGetStatusJson());

    public void Dispose() => _handle.Dispose();
}
