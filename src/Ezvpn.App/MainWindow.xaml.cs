using Ezvpn.App.ViewModels;
using Ezvpn.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ezvpn.App;

public sealed partial class MainWindow : Window
{
    private readonly TunnelsManager _manager;

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

        Closed += (_, _) => _manager.DisconnectInternal();
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
}
