using System.IO;
using System.Runtime.InteropServices;
using Ezvpn.App.Services;
using Ezvpn.App.ViewModels;
using Ezvpn.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ezvpn.App;

public sealed partial class MainWindow : Window
{
    private readonly TunnelsManager _manager;
    private readonly IntPtr _hwnd;
    private TrayIcon? _tray;
    private TunnelViewModel? _trackedActive;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        Title = "ezvpn";
        _manager = new TunnelsManager(DispatcherQueue);
        RootGrid.DataContext = _manager;

        if (_manager.Tunnels.Count > 0)
        {
            TunnelList.SelectedIndex = 0;
        }

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
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
        // nothing to hide to, so fall back to the plain quit behavior.
        AppWindow.Closing += (_, e) =>
        {
            if (_tray is not null && !_isExiting)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };

        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
            // Last-resort teardown on window close (e.g. the no-tray fallback where
            // closing quits). Block until routes/adapter are removed — the window is
            // already gone, so there is no live UI to keep responsive. In the Quit
            // path the session is already torn down, so this is a no-op.
            _manager.ShutdownSync();
        };
    }

    private void SetUpTray(string iconPath, string grayIconPath)
    {
        try
        {
            _tray = new TrayIcon(_hwnd, iconPath, grayIconPath, "ezvpn");
        }
        catch (Exception)
        {
            // The tray icon is a convenience, not load-bearing. If it fails to
            // register, run as a plain windowed app (with _tray null, closing the
            // window quits) rather than failing startup.
            _tray = null;
            return;
        }

        // Reflect the active tunnel's connection state in the tray icon (gray
        // until connected). The active tunnel changes as tunnels connect/switch,
        // so re-subscribe to whichever view model is currently active. This works
        // whether the window is shown or hidden to the tray: closing only hides
        // the window (see AppWindow.Closing above), so these subscriptions and the
        // manager's poll timer keep running until the app actually quits.
        _manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TunnelsManager.Active))
            {
                TrackActiveConnection();
            }
        };
        TrackActiveConnection();

        _tray.MenuStateProvider = () =>
            (Selected?.CanConnect ?? false, Selected?.CanDisconnect ?? false);
        _tray.Toggled += ToggleWindow;
        _tray.ShowRequested += ShowMainWindow;
        _tray.ConnectRequested += async () =>
        {
            if (Selected is { } vm)
            {
                await _manager.ConnectAsync(vm);
            }
        };
        _tray.DisconnectRequested += () =>
        {
            if (Selected is { } vm)
            {
                _manager.Disconnect(vm);
            }
        };
        _tray.QuitRequested += Quit;
    }

    /// <summary>
    /// Point the tray's connection state at the currently active tunnel: unhook
    /// the previous view model, hook the new one, and refresh the icon now.
    /// </summary>
    private void TrackActiveConnection()
    {
        if (_trackedActive is not null)
        {
            _trackedActive.PropertyChanged -= ActiveConnection_PropertyChanged;
        }
        _trackedActive = _manager.Active;
        if (_trackedActive is not null)
        {
            _trackedActive.PropertyChanged += ActiveConnection_PropertyChanged;
        }
        UpdateTrayConnected();
    }

    private void ActiveConnection_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Empty name is a bulk "everything changed" (raised on each status poll);
        // IsConnected covers the explicit state transitions.
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(TunnelViewModel.IsConnected))
        {
            UpdateTrayConnected();
        }
    }

    private void UpdateTrayConnected() => _tray?.SetConnected(_manager.Active?.IsConnected ?? false);

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
        _isExiting = true;
        _tray?.Dispose();
        _tray = null;
        // Await teardown so routes/adapter are removed before we exit, while the UI
        // thread keeps pumping (no freeze). The blocking native stop runs on a
        // background thread inside DisconnectAsync.
        await _manager.DisconnectAsync();
        Application.Current.Exit();
    }

    private TunnelViewModel? Selected => TunnelList.SelectedItem as TunnelViewModel;

    private void TunnelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteButton.IsEnabled = Selected is not null;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TunnelEditDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add profile",
        };
        dialog.SetExistingNames(_manager.Tunnels.Select(t => t.Name));

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var (profile, token, relayToken) = dialog.BuildResult();
            var vm = _manager.Add(profile, token, relayToken);
            TunnelList.SelectedItem = vm;
        }
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } vm)
        {
            return;
        }

        var dialog = new TunnelEditDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Edit profile",
        };
        dialog.SetExistingNames(_manager.Tunnels.Where(t => t != vm).Select(t => t.Name));
        dialog.LoadFrom(vm.Profile, _manager.LoadToken(vm), _manager.LoadRelayToken(vm));

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var token = dialog.ApplyTo(vm.Profile);
            _manager.Update(vm, token, dialog.RelayToken);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } vm)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete profile",
            Content = $"Delete \"{vm.Name}\"? This also removes its stored auth token.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            _manager.Delete(vm);
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
