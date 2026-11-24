using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ezvpn.Core.Interop;

/// <summary>
/// Owns the opaque <c>EzvpnHandle*</c> returned by <c>ezvpn_start</c>. Releasing
/// it calls <c>ezvpn_stop</c>, which signals the tunnel to stop and blocks until
/// routes/adapter teardown completes. It also frees the <see cref="GCHandle"/>
/// passed to <c>ezvpn_start</c> as the event callback's <c>ctx</c> — only after
/// <c>ezvpn_stop</c> returns, since no event is delivered after that.
/// </summary>
internal sealed class EzvpnSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GCHandle _eventContext;

    private EzvpnSafeHandle() : base(ownsHandle: true)
    {
    }

    internal static EzvpnSafeHandle FromPtr(IntPtr ptr, GCHandle eventContext)
    {
        var h = new EzvpnSafeHandle();
        h.SetHandle(ptr);
        h._eventContext = eventContext;
        return h;
    }

    protected override bool ReleaseHandle()
    {
        EzvpnNative.Stop(handle);
        _eventContext.Free();
        return true;
    }
}
