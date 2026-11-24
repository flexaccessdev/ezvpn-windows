using System.Runtime.InteropServices;

namespace Ezvpn.App.Services;

/// <summary>
/// A Windows notification-area (system tray) icon with a native context menu.
///
/// WinUI 3 has no tray API, so this is hand-rolled on Shell_NotifyIcon (matching
/// the app's other explicit P/Invoke, e.g. the Credential Manager). The tray
/// callbacks are delivered as window messages, so the owning window's WndProc is
/// subclassed to intercept them; everything else is chained to the original proc.
/// The right-click menu is a classic Win32 popup (TrackPopupMenuEx) rather than a
/// XAML flyout, which sidesteps the focus/dismiss problems flyouts have when no
/// WinUI window is active.
///
/// All callbacks are raised on the UI thread (the window proc runs on the thread
/// that owns the window), so handlers may touch the UI directly.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>One profile row in the tray menu.</summary>
    /// <param name="Name">Display name.</param>
    /// <param name="IsCurrent">Whether this is the tunnel that is up or coming up (checked).</param>
    /// <param name="StateText">Shown after the name for the current tunnel, e.g. "Connecting…".</param>
    /// <param name="Key">Opaque value handed back by <see cref="ProfileRequested"/>.</param>
    public sealed record ProfileItem(string Name, bool IsCurrent, string? StateText, object Key);

    /// <summary>What the context menu shows, captured when it opens.</summary>
    public sealed record MenuState(IReadOnlyList<ProfileItem> Profiles, bool CanDisconnect);

    // Tray callback message (WM_APP range is reserved for app-private messages).
    private const uint WM_TRAYICON = 0x8000 + 1;
    private const uint WM_NULL = 0x0000;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int GWLP_WNDPROC = -4;

    private const uint NIM_ADD = 0x0;
    private const uint NIM_MODIFY = 0x1;
    private const uint NIM_DELETE = 0x2;
    private const uint NIM_SETVERSION = 0x4;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    // Selects the callback contract. Version 3 keeps the legacy per-event mouse
    // messages (WM_LBUTTONUP / WM_RBUTTONUP) that WndProc parses; version 4 would
    // switch left-click to NIN_SELECT and move the anchor coords into wParam,
    // which would require reworking the message handling below.
    private const uint NOTIFYICON_VERSION = 3;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private const uint MF_STRING = 0x0;
    private const uint MF_GRAYED = 0x1;
    private const uint MF_CHECKED = 0x8;
    private const uint MF_SEPARATOR = 0x800;

    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint TPM_NONOTIFY = 0x80;
    private const uint TPM_RETURNCMD = 0x100;

    // Menu command ids.
    private const uint IdShow = 1;
    private const uint IdDisconnect = 3;
    private const uint IdQuit = 4;
    // Profile rows are IdProfileBase + their index in the menu snapshot.
    private const uint IdProfileBase = 100;

    // The shell's szTip holds 128 UTF-16 units including the terminator.
    private const int MaxTooltipLength = 127;

    private readonly IntPtr _hwnd;
    private readonly WndProcDelegate _wndProc; // kept alive; the OS holds a raw pointer.
    private readonly IntPtr _oldWndProc;
    private readonly string? _versionLabel;

    private NOTIFYICONDATAW _data;
    // The tooltip the shell last accepted; a failed update leaves it unchanged so
    // the same text is retried on the next call instead of being skipped.
    private string? _appliedTooltip;
    private IntPtr _connectedIcon;
    private IntPtr _disconnectedIcon;
    private bool _connected;
    private bool _disposed;

    /// <summary>Left-click on the icon (toggle the window).</summary>
    public event Action? Toggled;

    /// <summary>Menu "Show" (always bring the window up).</summary>
    public event Action? ShowRequested;

    /// <summary>A profile row was picked; carries its <see cref="ProfileItem.Key"/>.</summary>
    public event Action<object>? ProfileRequested;
    public event Action? DisconnectRequested;
    public event Action? QuitRequested;

    /// <summary>
    /// Supplies the profiles to list and whether Disconnect is available, read
    /// each time the menu opens.
    /// </summary>
    public Func<MenuState>? MenuStateProvider { get; set; }

    /// <param name="hwnd">The owning window's HWND (its WndProc is subclassed).</param>
    /// <param name="connectedIconPath">Path to the .ico shown while connected.</param>
    /// <param name="disconnectedIconPath">
    /// Path to the (gray) .ico shown while not connected; the tray starts here.
    /// </param>
    /// <param name="tooltip">Hover tooltip (truncated to 127 chars by the shell).</param>
    /// <param name="versionLabel">
    /// Optional version string shown as a disabled header at the top of the
    /// context menu (e.g. "v0.1.0"). Omitted from the menu when null/empty.
    /// </param>
    public TrayIcon(IntPtr hwnd, string connectedIconPath, string disconnectedIconPath, string tooltip, string? versionLabel = null)
    {
        _hwnd = hwnd;
        _versionLabel = versionLabel;

        _connectedIcon = LoadTrayIcon(connectedIconPath);
        try
        {
            _disconnectedIcon = LoadTrayIcon(disconnectedIconPath);
        }
        catch
        {
            DestroyIcon(_connectedIcon);
            _connectedIcon = IntPtr.Zero;
            throw;
        }

        _wndProc = WndProc;
        _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

        // Start disconnected (gray): the tunnel is not connected until proven so.
        _data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _disconnectedIcon,
            szTip = tooltip,
        };
        if (!Shell_NotifyIcon(NIM_ADD, ref _data))
        {
            // Roll back the partial initialization so we don't leak the icons or
            // leave the window permanently subclassed for an icon that isn't there.
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
            DestroyIcon(_connectedIcon);
            DestroyIcon(_disconnectedIcon);
            _connectedIcon = IntPtr.Zero;
            _disconnectedIcon = IntPtr.Zero;
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed to register the tray icon.");
        }

        _appliedTooltip = tooltip;

        // Opt into a defined callback contract instead of the implicit default.
        _data.uVersionOrTimeout = NOTIFYICON_VERSION;
        Shell_NotifyIcon(NIM_SETVERSION, ref _data);
    }

    // Load at the small-icon size so the shell doesn't have to downscale a large
    // frame; the multi-resolution .ico carries a 16/20/24/32 frame.
    private static IntPtr LoadTrayIcon(string iconPath)
    {
        var icon = LoadImage(
            IntPtr.Zero, iconPath, IMAGE_ICON,
            GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON),
            LR_LOADFROMFILE);
        if (icon == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to load tray icon from '{iconPath}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        return icon;
    }

    /// <summary>
    /// Switch the tray icon between the connected (teal) and not-connected (gray)
    /// variants. No-op if the state is unchanged.
    /// </summary>
    public void SetConnected(bool connected)
    {
        if (_disposed || connected == _connected)
        {
            return;
        }
        _connected = connected;
        _data.hIcon = connected ? _connectedIcon : _disconnectedIcon;
        _data.uFlags = NIF_ICON;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    /// <summary>Update the hover tooltip (e.g. to reflect connection state).</summary>
    public void SetTooltip(string tooltip)
    {
        if (tooltip.Length > MaxTooltipLength)
        {
            tooltip = tooltip[..(MaxTooltipLength - 1)] + "…";
        }
        // Called on every status poll; only bother the shell on a change.
        if (_disposed || tooltip == _appliedTooltip)
        {
            return;
        }
        _data.szTip = tooltip;
        _data.uFlags = NIF_TIP;
        _appliedTooltip = Shell_NotifyIcon(NIM_MODIFY, ref _data) ? tooltip : null;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    Toggled?.Invoke();
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowContextMenu();
                    break;
            }
            return IntPtr.Zero;
        }
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var state = MenuStateProvider?.Invoke() ?? new MenuState(Array.Empty<ProfileItem>(), false);

        IntPtr menu = CreatePopupMenu();
        try
        {
            if (!string.IsNullOrEmpty(_versionLabel))
            {
                // Non-interactive header (no command id) showing the app version.
                AppendMenu(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero, $"ezvpn {_versionLabel}");
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            }
            AppendMenu(menu, MF_STRING, (UIntPtr)IdShow, "Show ezvpn");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            // One row per profile: pick one to connect it (switching away from
            // whatever is up); the current tunnel is checked and shows its state.
            if (state.Profiles.Count == 0)
            {
                AppendMenu(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero, "No profiles yet");
            }
            for (var i = 0; i < state.Profiles.Count; i++)
            {
                var p = state.Profiles[i];
                // '&' marks a mnemonic in menu text; double it to show it literally.
                var label = p.Name.Replace("&", "&&");
                if (p.IsCurrent && !string.IsNullOrEmpty(p.StateText))
                {
                    label += "\t" + p.StateText;
                }
                AppendMenu(menu, MF_STRING | (p.IsCurrent ? MF_CHECKED : 0), (UIntPtr)(IdProfileBase + (uint)i), label);
            }
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING | (state.CanDisconnect ? 0 : MF_GRAYED), (UIntPtr)IdDisconnect, "Disconnect");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)IdQuit, "Quit ezvpn");

            GetCursorPos(out POINT pt);
            // Required for the menu to dismiss when the user clicks elsewhere, and
            // the trailing WM_NULL is the documented workaround for a stale menu.
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenuEx(
                menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case IdShow: ShowRequested?.Invoke(); break;
                case IdDisconnect: DisconnectRequested?.Invoke(); break;
                case IdQuit: QuitRequested?.Invoke(); break;
                case >= IdProfileBase when cmd - IdProfileBase < state.Profiles.Count:
                    ProfileRequested?.Invoke(state.Profiles[(int)(cmd - IdProfileBase)].Key);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Shell_NotifyIcon(NIM_DELETE, ref _data);
        if (_oldWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
        }
        if (_connectedIcon != IntPtr.Zero)
        {
            DestroyIcon(_connectedIcon);
            _connectedIcon = IntPtr.Zero;
        }
        if (_disconnectedIcon != IntPtr.Zero)
        {
            DestroyIcon(_disconnectedIcon);
            _disconnectedIcon = IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
