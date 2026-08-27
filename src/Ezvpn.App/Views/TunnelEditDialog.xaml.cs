using Ezvpn.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ezvpn.App.Views;

/// <summary>Add/edit form for a <see cref="TunnelProfile"/>.</summary>
public sealed partial class TunnelEditDialog : ContentDialog
{
    private string[] _existingNames = Array.Empty<string>();

    public TunnelEditDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Set when the user asked for the key manager. WinUI allows only one
    /// <see cref="ContentDialog"/> at a time, so this dialog hides itself and the
    /// caller shows the keys dialog and then re-shows this one — the same
    /// instance, so the half-filled form survives the round trip.
    /// </summary>
    public bool ManageKeysRequested { get; private set; }

    /// <summary>Names to reject as duplicates (exclude the profile being edited).</summary>
    public void SetExistingNames(IEnumerable<string> names) => _existingNames = names.ToArray();

    /// <summary>
    /// Fill the auth-key picker, keeping whatever key is currently selected (by
    /// id) if it is still listed. Call again after the keys dialog has been open.
    /// </summary>
    public void SetKeys(IReadOnlyList<AuthKeyStore.Key> keys)
    {
        var selectedId = SelectedKeyId;
        KeyBox.ItemsSource = keys;
        KeyBox.SelectedItem = keys.FirstOrDefault(k => k.Id == selectedId);
        // A key that was selected and is now gone (deleted from the key manager
        // this dialog just came back from) empties the picker, so explain it the
        // same way LoadFrom does rather than leaving it blank.
        if (selectedId is not null && KeyBox.SelectedItem is null)
        {
            MissingKeyBar.IsOpen = true;
        }
        UpdatePublicKeyText();
    }

    /// <summary>The id of the selected auth key, or null when none is selected.</summary>
    public string? SelectedKeyId => (KeyBox.SelectedItem as AuthKeyStore.Key)?.Id;

    /// <summary>Prefill the form from an existing profile + its stored relay token.</summary>
    public void LoadFrom(TunnelProfile profile, string? relayToken)
    {
        NameBox.Text = profile.Name;
        NodeIdBox.Text = profile.ServerNodeId;
        RelayBox.Text = string.Join(", ", profile.RelayUrls);
        RelayTokenBox.Password = relayToken ?? "";
        RoutesBox.Text = string.Join(", ", profile.Routes);
        Routes6Box.Text = string.Join(", ", profile.Routes6);
        AutoReconnectCheck.IsChecked = profile.AutoReconnect;
        MaxAttemptsBox.Value = profile.MaxReconnectAttempts ?? double.NaN;
        UpdateRelayTokenEnabled();

        var keys = (IReadOnlyList<AuthKeyStore.Key>?)KeyBox.ItemsSource ?? Array.Empty<AuthKeyStore.Key>();
        KeyBox.SelectedItem = keys.FirstOrDefault(k => k.Id == profile.AuthKeyId);
        // Nothing to preselect: the key was deleted from the list, or the profile
        // predates key auth. Say so rather than showing an empty picker with no
        // explanation — a saved profile always names a key otherwise.
        MissingKeyBar.IsOpen = KeyBox.SelectedItem is null;
        UpdatePublicKeyText();
    }

    /// <summary>The optional relay token from the form, or null when blank.</summary>
    public string? RelayToken =>
        string.IsNullOrWhiteSpace(RelayTokenBox.Password) ? null : RelayTokenBox.Password;

    /// <summary>Build a brand-new profile and its relay token from the form (for Add).</summary>
    public (TunnelProfile Profile, string? RelayToken) BuildResult()
    {
        var profile = new TunnelProfile();
        ApplyTo(profile);
        return (profile, RelayToken);
    }

    /// <summary>
    /// Write the form into <paramref name="profile"/>. Only the selected key's
    /// <em>id</em> lands on the profile; its secret is copied into the profile's
    /// own credential by <c>TunnelsManager</c>. The optional relay token is read
    /// separately via <see cref="RelayToken"/>.
    /// </summary>
    public void ApplyTo(TunnelProfile profile)
    {
        profile.Name = NameBox.Text.Trim();
        profile.ServerNodeId = NodeIdBox.Text.Trim();
        profile.AuthKeyId = SelectedKeyId ?? "";
        profile.RelayUrls = TunnelValidation.SplitList(RelayBox.Text);
        profile.Routes = TunnelValidation.SplitList(RoutesBox.Text);
        profile.Routes6 = TunnelValidation.SplitList(Routes6Box.Text);
        profile.AutoReconnect = AutoReconnectCheck.IsChecked ?? true;
        profile.MaxReconnectAttempts = ParseMaxAttempts(MaxAttemptsBox.Value);
    }

    // Hide (rather than close) so the caller can re-show this instance with the
    // form intact once the keys dialog is done. Hide() reports None, which the
    // caller distinguishes from Cancel by ManageKeysRequested.
    private void OnManageKeys(object sender, RoutedEventArgs args)
    {
        ManageKeysRequested = true;
        Hide();
    }

    /// <summary>Cleared by the caller once it has shown the keys dialog.</summary>
    public void ClearManageKeysRequest() => ManageKeysRequested = false;

    private void OnKeySelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (KeyBox.SelectedItem is not null)
        {
            MissingKeyBar.IsOpen = false;
        }
        UpdatePublicKeyText();
    }

    // Show the selected key's public half: it is what has to be on the server's
    // authorized_keys file, and it is not a secret.
    private void UpdatePublicKeyText()
    {
        if (KeyBox.SelectedItem is AuthKeyStore.Key key)
        {
            PublicKeyText.Text = $"Public key (put this on the server): {key.PublicKey}";
            PublicKeyText.Visibility = Visibility.Visible;
        }
        else
        {
            PublicKeyText.Text = "";
            PublicKeyText.Visibility = Visibility.Collapsed;
        }
    }

    // The relay token is only meaningful with custom relays: disable (and clear)
    // the field whenever no relay URLs are entered.
    private void OnRelayBoxTextChanged(object sender, TextChangedEventArgs args) =>
        UpdateRelayTokenEnabled();

    private void UpdateRelayTokenEnabled()
    {
        var hasRelays = TunnelValidation.SplitList(RelayBox.Text).Count > 0;
        RelayTokenBox.IsEnabled = hasRelays;
        if (!hasRelays)
        {
            RelayTokenBox.Password = "";
        }
    }

    /// <summary>
    /// Accept only a finite whole number in [1, uint.MaxValue]; NaN, infinity,
    /// fractional, and out-of-range values are treated as "unset" (null) rather
    /// than being silently truncated or overflowing on the cast.
    /// </summary>
    private static uint? ParseMaxAttempts(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }
        if (value < 1 || value > uint.MaxValue)
        {
            return null;
        }
        if (value != Math.Floor(value))
        {
            return null;
        }
        return (uint)value;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var error = Validate();
        if (error is not null)
        {
            ErrorBar.Message = error;
            ErrorBar.IsOpen = true;
            args.Cancel = true;
        }
    }

    private string? Validate()
    {
        var nameError = TunnelValidation.ValidateName(NameBox.Text, _existingNames);
        if (nameError is not null)
        {
            return nameError;
        }

        var nodeError = TunnelValidation.ValidateServerNodeId(NodeIdBox.Text);
        if (nodeError is not null)
        {
            return nodeError;
        }

        var keyError = TunnelValidation.ValidateAuthKeyId(SelectedKeyId);
        if (keyError is not null)
        {
            return keyError;
        }

        // The relay token is custom-relay-only (the core rejects it otherwise).
        // The field is normally auto-cleared when no relays are present; this
        // guards the edge case and gives a clear message. Relay URL *format* is
        // validated by the core at connect time, matching ezvpn-apple (which does
        // no client-side URL validation either).
        if (!string.IsNullOrWhiteSpace(RelayTokenBox.Password)
            && TunnelValidation.SplitList(RelayBox.Text).Count == 0)
        {
            return "A relay token requires at least one relay URL.";
        }

        var routes4 = TunnelValidation.SplitList(RoutesBox.Text);
        var r4Error = TunnelValidation.ValidateRoutes(routes4, ipv6: false);
        if (r4Error is not null)
        {
            return r4Error;
        }

        var routes6 = TunnelValidation.SplitList(Routes6Box.Text);
        var r6Error = TunnelValidation.ValidateRoutes(routes6, ipv6: true);
        if (r6Error is not null)
        {
            return r6Error;
        }

        return null;
    }
}
