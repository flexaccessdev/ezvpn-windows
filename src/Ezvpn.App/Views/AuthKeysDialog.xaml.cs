using System.Collections.ObjectModel;
using Ezvpn.Core;
using Ezvpn.Core.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Ezvpn.App.Views;

/// <summary>
/// The auth-key manager: the app's shared, named ed25519 keys, with generate,
/// import (paste a secret from another device), rename, copy and delete — the
/// Windows counterpart of the Apple/Android "Keys" screen.
///
/// Public keys show unmasked (they are not secrets); secrets never render —
/// copying one goes straight to the clipboard, behind a confirmation. Every
/// action is inline (rename in place, confirmation flyouts) because WinUI allows
/// only one <see cref="ContentDialog"/> open at a time, so the nested prompts
/// the other platforms use are not available here.
/// </summary>
public sealed partial class AuthKeysDialog : ContentDialog
{
    private readonly AuthKeyStore _store;

    /// <summary>
    /// Set while <see cref="Rebuild"/> tears the rows down. Removing a row that
    /// holds focus raises <c>LostFocus</c> on its name box, which would re-enter
    /// the rename path (and rebuild again) for a name that just committed.
    /// </summary>
    private bool _rebuilding;

    /// <summary>
    /// The key whose confirmation flyout is open. Captured when the flyout opens
    /// rather than bound inside it, because flyout content is hosted outside the
    /// row's visual tree and does not reliably inherit its DataContext.
    /// </summary>
    private string? _confirmKeyId;

    public AuthKeysDialog(AuthKeyStore store)
    {
        InitializeComponent();
        _store = store;
        KeyList.ItemsSource = Keys;
        Rebuild();

        // A store that couldn't be read shows an empty list but refuses every
        // write, so say why up front rather than at the first failed add.
        if (_store.LoadError is not null)
        {
            ShowError(_store.LoadError);
        }
    }

    /// <summary>The keys on screen; rebuilt wholesale after every mutation.</summary>
    private ObservableCollection<AuthKeyStore.Key> Keys { get; } = new();

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            Keys.Clear();
            foreach (var key in _store.Keys)
            {
                Keys.Add(key);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        var empty = Keys.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        KeyList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAddKey(object sender, RoutedEventArgs args)
    {
        // A blank secret means "generate one for me"; anything else is an import.
        var secret = NewSecretBox.Password;
        if (string.IsNullOrWhiteSpace(secret))
        {
            var generated = AuthKey.Generate();
            if (generated is null)
            {
                ShowError("Key generation failed.");
                return;
            }
            secret = generated.SecretKey;
        }

        var error = _store.Add(NewNameBox.Text, secret, out _);
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        NewNameBox.Text = "";
        NewSecretBox.Password = "";
        ErrorBar.IsOpen = false;
        Rebuild();
    }

    /// <summary>
    /// Commit an in-place rename. On failure the box goes back to the stored
    /// name, so what is on screen is always what is stored.
    /// </summary>
    private void OnKeyNameLostFocus(object sender, RoutedEventArgs args)
    {
        if (_rebuilding || sender is not TextBox { Tag: string id } box)
        {
            return;
        }

        var error = _store.Rename(id, box.Text);
        if (error is not null)
        {
            ShowError(error);
            box.Text = _store.Find(id)?.Name ?? box.Text;
            return;
        }
        Rebuild();
    }

    private void OnKeyNameKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter)
        {
            return;
        }
        args.Handled = true;
        // Move focus off the box so the rename commits through LostFocus — one
        // code path for both ways of finishing an edit.
        AddKeyButton.Focus(FocusState.Programmatic);
    }

    private void OnCopyPublicKey(object sender, RoutedEventArgs args)
    {
        if (KeyFor(sender) is { } key)
        {
            CopyToClipboard(key.PublicKey);
        }
    }

    /// <summary>
    /// Copy a secret key. Unlike the public half it is kept out of clipboard
    /// history (Win+V) and off the Cloud Clipboard, so the most sensitive thing
    /// the app holds isn't left sitting in a history pane or synced to the user's
    /// other machines. The Apple app's expiring pasteboard copy is the same idea;
    /// Windows has no expiry, so this is the closest equivalent.
    /// </summary>
    private void CopySecretToClipboard(string secret)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(secret);
            Clipboard.SetContentWithOptions(
                package,
                new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't copy to the clipboard: {ex.Message}");
        }
    }

    /// <summary>Remember which row's confirmation is being shown.</summary>
    private void OnConfirmFlyoutOpening(object? sender, object args) =>
        _confirmKeyId = ((sender as FlyoutBase)?.Target as FrameworkElement)?.Tag as string;

    private void OnCopySecretKey(object sender, RoutedEventArgs args)
    {
        CloseFlyout(sender);
        if (ConfirmedKey() is { } key)
        {
            CopySecretToClipboard(key.Secret);
        }
    }

    private void OnDeleteKey(object sender, RoutedEventArgs args)
    {
        CloseFlyout(sender);
        if (ConfirmedKey() is not { } key)
        {
            return;
        }

        var error = _store.Delete(key.Id);
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        Rebuild();
    }

    /// <summary>The key a row's button belongs to (its Tag carries the key id).</summary>
    private AuthKeyStore.Key? KeyFor(object sender) =>
        sender is FrameworkElement { Tag: string id } ? _store.Find(id) : null;

    /// <summary>
    /// The key the open confirmation belongs to. Says so rather than doing
    /// nothing if the flyout could not be tied back to a row.
    /// </summary>
    private AuthKeyStore.Key? ConfirmedKey()
    {
        var key = _confirmKeyId is null ? null : _store.Find(_confirmKeyId);
        if (key is null)
        {
            ShowError("That key is no longer in the list.");
        }
        return key;
    }

    /// <summary>
    /// Dismiss the confirmation flyout the clicked button sits in. Only that
    /// flyout's own popup is closed — the dialog itself is hosted in a popup too.
    /// </summary>
    private static void CloseFlyout(object sender)
    {
        for (DependencyObject? node = sender as DependencyObject;
             node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is FlyoutPresenter { Parent: Popup popup })
            {
                popup.IsOpen = false;
                return;
            }
        }
    }

    private void CopyToClipboard(string value)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(value);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process; that is not worth
            // taking the dialog down for.
            ShowError($"Couldn't copy to the clipboard: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
