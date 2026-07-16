using System.Text;

namespace Ezvpn.Core.Interop;

/// <summary>Thrown when <c>ezvpn_start</c> fails during setup.</summary>
public sealed class EzvpnException : Exception
{
    public EzvpnException(string message) : base(message)
    {
    }
}

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
    /// Start the client from an <c>ezvpn_start</c> config JSON string (build it
    /// with <see cref="EzvpnConfig.Build"/>). Returns once *started*; poll
    /// <see cref="TryGetStatus"/> for the connected state. Throws
    /// <see cref="EzvpnException"/> on a setup failure.
    /// </summary>
    public static EzvpnSession Start(string configJson)
    {
        var err = new byte[4096];
        var ptr = EzvpnNative.Start(configJson, err, (nuint)err.Length);
        if (ptr == IntPtr.Zero)
        {
            var msg = ReadCString(err);
            throw new EzvpnException(msg.Length > 0 ? msg : "ezvpn_start failed");
        }
        return new EzvpnSession(EzvpnSafeHandle.FromPtr(ptr));
    }

    /// <summary>
    /// Snapshot the live status JSON, or null if unavailable (disposed / null
    /// handle). Grows the buffer automatically if the snapshot is large.
    /// </summary>
    public string? TryGetStatusJson()
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
                var rc = EzvpnNative.Status(ptr, buf, (nuint)buf.Length);
                if (rc < 0)
                {
                    return null;
                }
                if (rc == 0)
                {
                    if (size >= MaxStatusBytes)
                    {
                        // Give up growing; return whatever fit (best effort).
                        return ReadCString(buf);
                    }
                    size = Math.Min(size * 2, MaxStatusBytes);
                    continue;
                }
                return ReadCString(buf);
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

    /// <summary>Decode a NUL-terminated UTF-8 buffer into a string.</summary>
    private static string ReadCString(byte[] buf)
    {
        var len = Array.IndexOf(buf, (byte)0);
        if (len < 0)
        {
            len = buf.Length;
        }
        return Encoding.UTF8.GetString(buf, 0, len);
    }
}
