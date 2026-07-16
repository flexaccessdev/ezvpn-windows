using System.Runtime.InteropServices;

namespace Ezvpn.Core.Interop;

/// <summary>
/// Raw P/Invoke declarations for <c>ezvpn.dll</c> (see <c>windows/ezvpn.h</c>).
/// Use <see cref="EzvpnSession"/> rather than calling these directly.
/// </summary>
internal static partial class EzvpnNative
{
    private const string Dll = "ezvpn";

    [LibraryImport(Dll, EntryPoint = "ezvpn_init_logging")]
    internal static partial void InitLogging();

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
}
