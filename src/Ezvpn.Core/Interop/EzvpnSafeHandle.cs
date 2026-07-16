using Microsoft.Win32.SafeHandles;

namespace Ezvpn.Core.Interop;

/// <summary>
/// Owns the opaque <c>EzvpnHandle*</c> returned by <c>ezvpn_start</c>. Releasing
/// it calls <c>ezvpn_stop</c>, which signals the tunnel to stop and blocks until
/// routes/adapter teardown completes.
/// </summary>
internal sealed class EzvpnSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private EzvpnSafeHandle() : base(ownsHandle: true)
    {
    }

    internal static EzvpnSafeHandle FromPtr(IntPtr ptr)
    {
        var h = new EzvpnSafeHandle();
        h.SetHandle(ptr);
        return h;
    }

    protected override bool ReleaseHandle()
    {
        EzvpnNative.Stop(handle);
        return true;
    }
}
