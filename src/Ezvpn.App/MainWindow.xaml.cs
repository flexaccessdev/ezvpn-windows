using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using Ezvpn.App.Services;
using Ezvpn.App.ViewModels;
using Ezvpn.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Ezvpn.App;

public sealed partial class MainWindow : Window
{
    // Initial and minimum window size, in DIPs (scaled to the window's DPI).
    private const int InitialWidth = 960;
    private const int InitialHeight = 680;
    private const int MinWidth = 760;
    private const int MinHeight = 520;

    private readonly TunnelsManager _manager;
    private readonly IntPtr _hwnd;
    private TrayIcon? _tray;
    private TunnelViewModel? _trackedCurrent;
    // Quit has begun (_isExiting) / has finished its shutdown (_shutDown): only
    // then may the window really close.
    private bool _isExiting;
    private bool _shutDown;

    /// <summary>Which first-run step the empty state's buttons act on.</summary>
    private enum EmptyMode
    {
        /// <summary>No keys and no profiles: start by creating a key.</summary>
        Welcome,
        /// <summary>Keys exist, no profiles yet.</summary>
        NoProfiles,
        /// <summary>Profiles exist but none is selected.</summary>
        NoSelection,
    }

    private EmptyMode _emptyMode;

    public MainWindow()
    {
        InitializeComponent();

        // A mock run says so, so its empty profile list isn't taken for lost data.
        Title = SecretStore.IsMemory ? "ezvpn (mock keychain)" : "ezvpn";
        VersionText.Text = SecretStore.IsMemory ? $"{AppInfo.Summary} · mock keychain" : AppInfo.Summary;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SizeWindow();

        _manager = new TunnelsManager(DispatcherQueue);
        RootGrid.DataContext = _manager;
        _manager.Tunnels.CollectionChanged += Tunnels_CollectionChanged;

        if (_manager.Tunnels.Count > 0)
        {
            TunnelList.SelectedIndex = 0;
        }
        UpdateEmptyState();
        AddShortcuts();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ezvpn.ico");
        var grayIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ezvpn-gray.ico");
        if (File.Exists(iconPath))
        {
            // Title-bar / Alt-Tab icon (the .exe already carries it as its Win32
            // icon via <ApplicationIcon>; this covers the live window too). The
            // gray variant is only used by the tray, so fall back to the colored
            // icon for both if it is missing.
            AppWindow.SetIcon(iconPath);
            SetUpTray(iconPath, File.Exists(grayIconPath) ? grayIconPath : iconPath);
        }

        // Closing the window hides to the tray instead of quitting; the tunnel
        // keeps running. Quit (tray menu) is the only path that exits. Because the
        // window is hidden and not destroyed, this MainWindow instance stays alive
        // while in the tray — its poll timer keeps ticking and its PropertyChanged
        // subscriptions keep firing, so the tray icon's connection state (see
        // SetUpTray) stays live while the window is closed. Without a tray there is
        // nothing to hide to, so closing quits — through Quit, like the tray's
        // Quit: the window stays until its async shutdown has finished, as does a
        // close while that shutdown runs.
        AppWindow.Closing += (_, e) =>
        {
            if (_shutDown)
            {
                return;
            }
            e.Cancel = true;
            AppWindow.Hide();
            if (_tray is null || _isExiting)
            {
                Quit();
            }
        };

        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
            // Last resort, for a close that did not wait for Quit's shutdown.
            // Every close does (see Closing above), so after Quit both are no-ops.
            _manager.ShutdownSync();
            AppLog.Stop();
        };
    }

    /// <summary>
    /// Open at a size that fits the two panes without crowding, and stop the
    /// window from being shrunk until the detail pane clips. AppWindow works in
    /// physical pixels, so scale from DIPs by the window's DPI.
    /// </summary>
    private void SizeWindow()
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(InitialWidth * scale), (int)(InitialHeight * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        }
    }

    // --- Empty state ----------------------------------------------------------

    private void Tunnels_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateEmptyState();

    /// <summary>
    /// Show the detail pane for the selected profile, or — with nothing to show —
    /// a guided first step instead of an empty form: create a key, then add a
    /// profile, then pick one.
    /// </summary>
    private void UpdateEmptyState()
    {
        if (Selected is not null)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_manager.Tunnels.Count > 0)
        {
            _emptyMode = EmptyMode.NoSelection;
            EmptyTitle.Text = "Select a profile";
            EmptyMessage.Text = "Pick a profile on the left to see its status and connect.";
            EmptyActions.Visibility = Visibility.Collapsed;
        }
        else if (_manager.AuthKeys.Keys.Count == 0)
        {
            _emptyMode = EmptyMode.Welcome;
            EmptyTitle.Text = "Welcome to ezvpn";
            EmptyMessage.Text =
                "Setting up takes two steps. First create an auth key and add its public key " +
                "to the server's authorized_keys file. Then add a profile with the server's node id.";
            EmptyPrimaryButton.Content = "Create an auth key";
            EmptySecondaryButton.Content = "Add profile";
            EmptyActions.Visibility = Visibility.Visible;
        }
        else
        {
            _emptyMode = EmptyMode.NoProfiles;
            EmptyTitle.Text = "Add your first profile";
            EmptyMessage.Text =
                "A profile names the server to connect to (its node id) and the auth key to " +
                "connect with. Make sure the key's public half is on the server.";
            EmptyPrimaryButton.Content = "Add profile";
            EmptySecondaryButton.Content = "Auth keys";
            EmptyActions.Visibility = Visibility.Visible;
        }
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private async void EmptyPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_emptyMode == EmptyMode.Welcome)
        {
            await ShowKeysDialogAsync();
        }
        else
        {
            await AddProfileAsync();
        }
    }

    private async void EmptySecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_emptyMode == EmptyMode.Welcome)
        {
            await AddProfileAsync();
        }
        else
        {
            await ShowKeysDialogAsync();
        }
    }

    // --- Tray -----------------------------------------------------------------

    private void SetUpTray(string iconPath, string grayIconPath)
    {
        try
        {
            _tray = new TrayIcon(_hwnd, iconPath, grayIconPath, "ezvpn — not connected", AppInfo.Version);
        }
        catch (Exception)
        {
            // The tray icon is a convenience, not load-bearing. If it fails to
            // register, run as a plain windowed app (with _tray null, closing the
            // window quits) rather than failing startup.
            _tray = null;
            return;
        }

        // Reflect the current tunnel in the tray icon (gray until connected) and
        // its tooltip. The current tunnel changes as tunnels connect/switch, so
        // re-subscribe to whichever view model is current. This works whether
        // the window is shown or hidden to the tray: closing only hides the
        // window (see AppWindow.Closing above), so these subscriptions and the
        // manager's poll timer keep running until the app actually quits.
        _manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TunnelsManager.Current) or nameof(TunnelsManager.Active))
            {
                TrackCurrentConnection();
            }
        };
        TrackCurrentConnection();

        // The menu lists every profile; picking one connects it. Connect and
        // Disconnect act on the tunnel that is actually up, not on whichever
        // profile happens to be selected in the (possibly hidden) window.
        _tray.MenuStateProvider = () => new TrayIcon.MenuState(
            _manager.Tunnels
                .Select(vm => new TrayIcon.ProfileItem(
                    vm.Name, ReferenceEquals(vm, _manager.Current), vm.StateText, vm))
                .ToList(),
            _manager.Current is not null);
        _tray.Toggled += ToggleWindow;
        _tray.ShowRequested += ShowMainWindow;
        _tray.ProfileRequested += async key =>
        {
            if (key is not TunnelViewModel vm || !_manager.Tunnels.Contains(vm))
            {
                return;
            }
            TunnelList.SelectedItem = vm;
            if (ReferenceEquals(vm, _manager.Current))
            {
                // Already up (or coming up): show it rather than restart it.
                ShowMainWindow();
            }
            else
            {
                await _manager.ConnectAsync(vm);
            }
        };
        _tray.DisconnectRequested += () =>
        {
            if (_manager.Current is { } vm)
            {
                _manager.Disconnect(vm);
            }
        };
        _tray.QuitRequested += Quit;
    }

    /// <summary>
    /// Point the tray at the current tunnel: unhook the previous view model, hook
    /// the new one, and refresh the icon and tooltip now.
    /// </summary>
    private void TrackCurrentConnection()
    {
        if (_trackedCurrent is not null)
        {
            _trackedCurrent.PropertyChanged -= CurrentConnection_PropertyChanged;
        }
        _trackedCurrent = _manager.Current;
        if (_trackedCurrent is not null)
        {
            _trackedCurrent.PropertyChanged += CurrentConnection_PropertyChanged;
        }
        UpdateTray();
    }

    private void CurrentConnection_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Empty name is a bulk "everything changed" (raised on each status poll
        // and on every state transition).
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(TunnelViewModel.IsConnected))
        {
            UpdateTray();
        }
    }

    private void UpdateTray()
    {
        if (_tray is null)
        {
            return;
        }
        var current = _manager.Current;
        _tray.SetConnected(current?.IsConnected ?? false);
        _tray.SetTooltip(current is null
            ? "ezvpn — not connected"
            : $"ezvpn — {current.Name}: {current.ListStatusText}");
    }

    private void ToggleWindow()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
        else
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow()
    {
        AppWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd);
    }

    private async void Quit()
    {
        if (_isExiting)
        {
            // Already on its way out.
            return;
        }
        _isExiting = true;
        _tray?.Dispose();
        _tray = null;
        // Await every native setup and teardown so routes/adapter are removed
        // before we exit, while the UI thread keeps pumping (no freeze). No
        // connect starts from here on. The blocking native work runs on
        // background threads.
        await _manager.ShutdownAsync();
        AppLog.Stop();
        _shutDown = true;
        Application.Current.Exit();
    }

    // --- Keyboard -------------------------------------------------------------

    /// <summary>Ctrl+N adds a profile, F2 edits the selected one.</summary>
    private void AddShortcuts()
    {
        AddShortcut(VirtualKey.N, VirtualKeyModifiers.Control, AddProfileAsync);
        AddShortcut(VirtualKey.F2, VirtualKeyModifiers.None, EditSelectedAsync);
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += async (_, args) =>
        {
            // Accelerators are window-wide; while a dialog or flyout is open the
            // keystroke belongs to it (and a second ContentDialog would throw).
            if (IsPopupOpen)
            {
                return;
            }
            args.Handled = true;
            await action();
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    // Delete is handled on the list only, so it never fires from a text box.
    private async void TunnelList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && Selected is not null && !IsPopupOpen)
        {
            e.Handled = true;
            await DeleteSelectedAsync();
        }
    }

    private bool IsPopupOpen =>
        Content.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0;

    // --- Profile actions ------------------------------------------------------

    private TunnelViewModel? Selected => TunnelList.SelectedItem as TunnelViewModel;

    private void TunnelList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEmptyState();

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await AddProfileAsync();

    private async Task AddProfileAsync()
    {
        var dialog = new TunnelEditDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add profile",
            PreselectSingleKey = true,
        };
        dialog.SetExistingNames(_manager.Tunnels.Select(t => t.Name));
        dialog.SetKeys(_manager.AuthKeys.Keys);

        if (await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            var (profile, relayToken) = dialog.BuildResult();
            var vm = _manager.Add(profile, relayToken);
            TunnelList.SelectedItem = vm;
        }
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e) => await EditSelectedAsync();

    private async Task EditSelectedAsync()
    {
        // Editing a running tunnel would leave the screen describing settings it
        // isn't using; the button says "Disconnect to edit" in that case.
        if (Selected is not { CanConnect: true } vm)
        {
            return;
        }

        var dialog = new TunnelEditDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Edit profile",
        };
        dialog.SetExistingNames(_manager.Tunnels.Where(t => t != vm).Select(t => t.Name));
        dialog.SetKeys(_manager.AuthKeys.Keys);
        dialog.LoadFrom(vm.Profile, _manager.LoadRelayToken(vm));

        if (await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            dialog.ApplyTo(vm.Profile);
            _manager.Update(vm, dialog.RelayToken);
        }
    }

    /// <summary>
    /// Show the profile editor, servicing its "Manage keys…" button. Only one
    /// <see cref="ContentDialog"/> can be open at a time, so the editor hides
    /// itself, the keys dialog runs, and then the *same* editor instance is shown
    /// again — the half-filled form survives, with a refreshed key picker.
    /// </summary>
    private async Task<ContentDialogResult> ShowEditDialogAsync(TunnelEditDialog dialog)
    {
        while (true)
        {
            var result = await dialog.ShowAsync();
            if (!dialog.ManageKeysRequested)
            {
                return result;
            }
            dialog.ClearManageKeysRequest();
            await ShowKeysDialogAsync();
            dialog.SetKeys(_manager.AuthKeys.Keys);
        }
    }

    private async void KeysButton_Click(object sender, RoutedEventArgs e) =>
        await ShowKeysDialogAsync();

    /// <summary>
    /// Run the auth-key manager, then re-read the key names the profile list and
    /// detail panel show (a key may have been renamed or deleted), and the
    /// first-run guidance (the first key may just have been made).
    /// </summary>
    private async Task ShowKeysDialogAsync()
    {
        var keys = new AuthKeysDialog(_manager.AuthKeys) { XamlRoot = Content.XamlRoot };
        await keys.ShowAsync();
        _manager.RefreshKeyNames();
        UpdateEmptyState();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { } vm)
        {
            return;
        }

        var running = ReferenceEquals(vm, _manager.Current);
        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Delete “{vm.Name}”?",
            Content = (running ? "Its tunnel is up and will be disconnected first. " : "") +
                "This removes the profile and its stored copy of the auth key. " +
                "The key itself stays in the auth key list.",
            PrimaryButtonText = running ? "Disconnect and delete" : "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // Keep a profile selected (the one that slid into this slot) rather than
        // falling back to the "select a profile" screen.
        var index = _manager.Tunnels.IndexOf(vm);
        _manager.Delete(vm);
        if (_manager.Tunnels.Count > 0)
        {
            TunnelList.SelectedIndex = Math.Min(index, _manager.Tunnels.Count - 1);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } vm)
        {
            await _manager.ConnectAsync(vm);
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } vm)
        {
            _manager.Disconnect(vm);
        }
    }

    private async void ConnPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } vm)
        {
            return;
        }
        var dialog = new ConnPathDialog(() => _manager.QueryConnPathAsync(vm))
        {
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void CopyNodeId_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } vm)
        {
            return;
        }
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(vm.ServerNodeId);
            Clipboard.SetContent(package);
            CopyFeedback.Flash(sender as Button);
        }
        catch (Exception)
        {
            // The clipboard can be held by another process; the id is selectable
            // text, so the user can still copy it by hand.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
