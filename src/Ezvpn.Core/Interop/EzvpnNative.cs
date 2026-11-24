using System.Runtime.InteropServices;
using System.Text;

namespace Ezvpn.Core.Interop;

/// <summary>
/// Raw P/Invoke declarations for <c>ezvpn.dll</c> (see <c>windows/ezvpn.h</c>).
/// Use <see cref="EzvpnSession"/> or <see cref="AuthKey"/> rather than calling
/// these directly.
/// </summary>
internal static partial class EzvpnNative
{
    private const string Dll = "ezvpn";

    [LibraryImport(Dll, EntryPoint = "ezvpn_init_logging")]
    internal static partial void InitLogging();

    /// <summary>
    /// Generate a fresh client keypair, written to <paramref name="outBuf"/> as
    /// <c>{"created":…,"public_key":"ed25519-pub:…","secret_key":"ed25519-sec:…"}</c>.
    /// Returns 1 on success, 0 if generation failed or the buffer was too small —
    /// in which case the buffer holds a truncated prefix of the document,
    /// <em>secret material included</em>, and must be zeroed.
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "ezvpn_generate_client_key")]
    internal static partial int GenerateClientKey(byte[] outBuf, nuint outLen);

    /// <summary>
    /// Derive the <c>ed25519-pub:…</c> public key of a secret key. Returns 1 with
    /// the public key in <paramref name="outBuf"/>, or 0 with an error message
    /// (or truncated output, if the buffer was too small).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "ezvpn_client_public_key", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ClientPublicKey(string secretKey, byte[] outBuf, nuint outLen);

    /// <summary><c>on_event</c> code: an attempt failed or the session was lost; retrying. Message: the error.</summary>
    internal const int EventReconnecting = 1;

    /// <summary><c>on_event</c> code: the tunnel is up (first connection or a reconnect).</summary>
    internal const int EventConnected = 2;

    /// <summary><c>on_event</c> code (final): the run loop ended and will not try again. Message: the reason, or null.</summary>
    internal const int EventEnded = 3;

    /// <summary>
    /// Start the client. Returns an opaque handle (or <see cref="IntPtr.Zero"/>
    /// on setup failure, in which case <paramref name="outBuf"/> holds a
    /// NUL-terminated UTF-8 error message). <paramref name="onEvent"/> is called
    /// from library threads with <paramref name="ctx"/> and an <c>Event*</c> code
    /// until <c>ezvpn_stop</c>, which waits out a call in progress.
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "ezvpn_start", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial IntPtr Start(
        string configJson,
        delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void> onEvent,
        IntPtr ctx,
        byte[] outBuf,
        nuint outLen);

    /// <summary>
    /// Write the status JSON into <paramref name="outBuf"/>. Returns 1 (full), 0
    /// (buffer too small; retry larger), or -1 (null handle).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "ezvpn_status")]
    internal static partial int Status(IntPtr handle, byte[] outBuf, nuint outLen);

    /// <summary>
    /// Write the connection-path JSON (every path plus custom-relay health) into
    /// <paramref name="outBuf"/>. Makes a <c>/healthz</c> request per custom
    /// relay, so call it on demand, not on the poll timer. Returns 1 (full), 0
    /// (buffer too small; retry larger), or -1 (null handle).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "ezvpn_conn_path")]
    internal static partial int ConnPath(IntPtr handle, byte[] outBuf, nuint outLen);

    [LibraryImport(Dll, EntryPoint = "ezvpn_stop")]
    internal static partial void Stop(IntPtr handle);

    /// <summary>Decode a NUL-terminated UTF-8 output buffer into a string.</summary>
    internal static string ReadCString(byte[] buf)
    {
        var len = Array.IndexOf(buf, (byte)0);
        if (len < 0)
        {
            len = buf.Length;
        }
        return Encoding.UTF8.GetString(buf, 0, len);
    }
}
