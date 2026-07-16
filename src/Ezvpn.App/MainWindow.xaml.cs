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
        if (File.Exists(iconPath))
        {
            // Title-bar / Alt-Tab icon (the .exe already carries it as its Win32
            // icon via <ApplicationIcon>; this covers the live window too).
            AppWindow.SetIcon(iconPath);
            SetUpTray(iconPath);
        }

        // Closing the window hides to the tray instead of quitting; the tunnel
        // keeps running. Quit (tray menu) is the only path that exits. Without a
        // tray there is nothing to hide to, so fall back to the plain quit behavior.
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
            _manager.DisconnectInternal();
        };
    }

    private void SetUpTray(string iconPath)
    {
        _tray = new TrayIcon(_hwnd, iconPath, "ezvpn");
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

    private void Quit()
    {
        _isExiting = true;
        _tray?.Dispose();
        _tray = null;
        _manager.DisconnectInternal();
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
            var (profile, token) = dialog.BuildResult();
            var vm = _manager.Add(profile, token);
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
        dialog.LoadFrom(vm.Profile, _manager.LoadToken(vm));

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var token = dialog.ApplyTo(vm.Profile);
            _manager.Update(vm, token);
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
