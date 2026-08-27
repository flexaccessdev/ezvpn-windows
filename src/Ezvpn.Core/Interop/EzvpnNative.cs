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

    /// <summary>
    /// Start the client. Returns an opaque handle (or <see cref="IntPtr.Zero"/>
    /// on setup failure, in which case <paramref name="outBuf"/> holds a
    /// NUL-terminated UTF-8 error message).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "ezvpn_start", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr Start(string configJson, byte[] outBuf, nuint outLen);

    /// <summary>
    /// Write the status JSON into <paramref name="outBuf"/>. Returns 1 (full), 0
    /// (buffer too small; retry larger), or -1 (null handle).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "ezvpn_status")]
    internal static partial int Status(IntPtr handle, byte[] outBuf, nuint outLen);

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
