using Ezvpn.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ezvpn.App.Views;

/// <summary>Add/edit form for a <see cref="TunnelProfile"/>.</summary>
public sealed partial class TunnelEditDialog : ContentDialog
{
    private string[] _existingNames = Array.Empty<string>();
    private bool _openedOnce;

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

    /// <summary>
    /// For a new profile: when exactly one key exists and none is picked yet,
    /// pick it — including the first key just made through "Manage keys…".
    /// Never set for an edit, where an empty picker means the profile's key was
    /// deleted and must be chosen deliberately.
    /// </summary>
    public bool PreselectSingleKey { get; set; }

    /// <summary>Names to reject as duplicates (exclude the profile being edited).</summary>
    public void SetExistingNames(IEnumerable<string> names) => _existingNames = names.ToArray();

    /// <summary>
    /// Fill the auth-key picker, keeping whatever key is currently selected (by
    /// id) if it is still listed. Call again after the keys dialog has been open.
    /// </summary>
    public void SetKeys(IReadOnlyList<AuthKeyStore.Key> keys)
    {
        var selectedId = SelectedKeyId;
        // Snapshot, never the caller's list: the store hands out its own live
        // list, which raises no collection-change notifications, so re-assigning
        // that same instance would leave the picker showing the items it was
        // built from — a key just added in the key manager would never appear.
        var snapshot = keys.ToArray();
        KeyBox.ItemsSource = snapshot;
        KeyBox.SelectedItem = snapshot.FirstOrDefault(k => k.Id == selectedId);
        if (KeyBox.SelectedItem is null && PreselectSingleKey && snapshot.Length == 1)
        {
            KeyBox.SelectedItem = snapshot[0];
        }
        // With no keys at all, say where one comes from instead of showing an
        // empty picker.
        NoKeysBar.IsOpen = snapshot.Length == 0;
        KeyBox.IsEnabled = snapshot.Length > 0;
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

    /// <summary>
    /// The selected key's id, for the save path only. <see cref="Validate"/>
    /// refuses to close the dialog without a selection, so this is where that
    /// invariant is cashed in — a profile never exists without a key.
    /// </summary>
    private string RequiredKeyId =>
        SelectedKeyId ?? throw new InvalidOperationException(
            "The profile editor was saved with no auth key selected.");

    /// <summary>Prefill the form from an existing profile + its stored relay token.</summary>
    public void LoadFrom(TunnelProfile profile, string? relayToken)
    {
        NameBox.Text = profile.Name;
        NodeIdBox.Text = profile.ServerNodeId;
        RelayBox.Text = string.Join(", ", profile.RelayUrls);
        RelayTokenBox.Password = relayToken ?? "";
        QuicAddrDiscoveryCheck.IsChecked = profile.RelayQuicAddrDiscovery;
        RoutesBox.Text = string.Join(", ", profile.Routes);
        Routes6Box.Text = string.Join(", ", profile.Routes6);
        AutoReconnectCheck.IsChecked = profile.AutoReconnect;
        MaxAttemptsBox.Value = profile.MaxReconnectAttempts ?? double.NaN;
        UpdateCustomRelayFieldsEnabled();
        UpdateMaxAttemptsEnabled();
        // Don't hide settings the profile actually uses behind a closed expander.
        AdvancedExpander.IsExpanded = profile.RelayUrls.Count > 0
            || !profile.AutoReconnect
            || profile.MaxReconnectAttempts is not null;

        var keys = (IReadOnlyList<AuthKeyStore.Key>?)KeyBox.ItemsSource ?? Array.Empty<AuthKeyStore.Key>();
        KeyBox.SelectedItem = keys.FirstOrDefault(k => k.Id == profile.AuthKeyId);
        // Nothing to preselect: the key this profile names has been deleted from
        // the list. Say so rather than showing an empty picker with no
        // explanation — a saved profile always names a key.
        MissingKeyBar.IsOpen = KeyBox.SelectedItem is null;
        UpdatePublicKeyText();
    }

    /// <summary>The optional relay token from the form, or null when blank.</summary>
    public string? RelayToken =>
        string.IsNullOrWhiteSpace(RelayTokenBox.Password) ? null : RelayTokenBox.Password;

    /// <summary>Build a brand-new profile and its relay token from the form (for Add).</summary>
    public (TunnelProfile Profile, string? RelayToken) BuildResult()
    {
        var profile = new TunnelProfile { AuthKeyId = RequiredKeyId };
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
        profile.AuthKeyId = RequiredKeyId;
        profile.RelayUrls = TunnelValidation.SplitList(RelayBox.Text);
        profile.RelayQuicAddrDiscovery = QuicAddrDiscoveryCheck.IsChecked ?? true;
        profile.Routes = TunnelValidation.SplitList(RoutesBox.Text);
        profile.Routes6 = TunnelValidation.SplitList(Routes6Box.Text);
        profile.AutoReconnect = AutoReconnectCheck.IsChecked ?? true;
        profile.MaxReconnectAttempts = ParseMaxAttempts(MaxAttemptsBox.Value);
    }

    // Focus the first field the first time the dialog opens (not again when it
    // re-shows after "Manage keys…", where the user was elsewhere in the form).
    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_openedOnce)
        {
            return;
        }
        _openedOnce = true;
        NameBox.Focus(FocusState.Programmatic);
    }

    // The attempt cap only means something while auto-reconnect is on.
    private void OnAutoReconnectChanged(object sender, RoutedEventArgs args) =>
        UpdateMaxAttemptsEnabled();

    // IsChecked="True" in the XAML raises Checked during InitializeComponent,
    // before MaxAttemptsBox (declared later) is assigned; it starts enabled,
    // which matches the checked default, so there is nothing to do yet.
    private void UpdateMaxAttemptsEnabled()
    {
        if (MaxAttemptsBox is not null)
        {
            MaxAttemptsBox.IsEnabled = AutoReconnectCheck.IsChecked ?? true;
        }
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
            PublicKeyText.Text = $"Public key — must be in the server's authorized_keys: {key.PublicKey}";
            PublicKeyText.Visibility = Visibility.Visible;
        }
        else
        {
            PublicKeyText.Text = "";
            PublicKeyText.Visibility = Visibility.Collapsed;
        }
    }

    // The relay token and QUIC address discovery are only meaningful with custom
    // relays (the core rejects either with the default relays). With no relay
    // URLs each is disabled, except while it holds a non-default value (a token,
    // discovery off): it then stays usable so it can be reset, rather than being
    // stuck behind a disabled control, and Save refuses it until it is reset or
    // a relay is added. Nothing is cleared behind the user's back, so emptying
    // the relay field while editing loses neither. Relays are counted as Save
    // parses them, so a stray comma is not a relay.
    private void OnRelayBoxTextChanged(object sender, TextChangedEventArgs args) =>
        UpdateCustomRelayFieldsEnabled();

    private void OnCustomRelayFieldChanged(object sender, RoutedEventArgs args) =>
        UpdateCustomRelayFieldsEnabled();

    // IsChecked="True" in the XAML raises Checked during InitializeComponent,
    // before every field is assigned; the XAML's starting state (both disabled,
    // no relays) is already right then, so there is nothing to do yet.
    private void UpdateCustomRelayFieldsEnabled()
    {
        if (RelayBox is null || RelayTokenBox is null || QuicAddrDiscoveryCheck is null)
        {
            return;
        }
        var hasRelays = TunnelValidation.SplitList(RelayBox.Text).Count > 0;
        RelayTokenBox.IsEnabled = hasRelays || RelayTokenBox.Password.Length > 0;
        QuicAddrDiscoveryCheck.IsEnabled = hasRelays || QuicAddrDiscoveryCheck.IsChecked == false;
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
        var (error, field) = Validate();
        if (error is not null)
        {
            ErrorBar.Message = error;
            ErrorBar.IsOpen = true;
            args.Cancel = true;
            // The form scrolls; make sure the message is on screen, then put the
            // cursor where the fix goes.
            FormScroller.ChangeView(null, 0, null);
            if (field == RelayBox)
            {
                AdvancedExpander.IsExpanded = true;
            }
            field?.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>The first problem with the form, and the field to fix it in.</summary>
    private (string? Error, Control? Field) Validate()
    {
        if (TunnelValidation.ValidateName(NameBox.Text, _existingNames) is { } nameError)
        {
            return (nameError, NameBox);
        }

        if (TunnelValidation.ValidateServerNodeId(NodeIdBox.Text) is { } nodeError)
        {
            return (nodeError, NodeIdBox);
        }

        if (TunnelValidation.ValidateAuthKeyId(SelectedKeyId) is { } keyError)
        {
            // With no keys to pick, the way forward is the key manager.
            return (keyError, KeyBox.IsEnabled ? (Control)KeyBox : ManageKeysButton);
        }

        // The relay token is custom-relay-only (the core rejects it otherwise).
        // It stays editable with no relays while it holds a value (see
        // UpdateCustomRelayFieldsEnabled), so this is where a token left with
        // no relays is refused. Relay URL *format* is
        // validated by the core at connect time, matching ezvpn-apple (which does
        // no client-side URL validation either).
        if (!string.IsNullOrWhiteSpace(RelayTokenBox.Password)
            && TunnelValidation.SplitList(RelayBox.Text).Count == 0)
        {
            return ("A relay token requires at least one relay URL.", RelayBox);
        }
        // Likewise for QUIC address discovery, which the default relays serve.
        if (QuicAddrDiscoveryCheck.IsChecked == false
            && TunnelValidation.SplitList(RelayBox.Text).Count == 0)
        {
            return ("QUIC address discovery can only be turned off with at least one relay URL.", RelayBox);
        }

        var routes4 = TunnelValidation.SplitList(RoutesBox.Text);
        if (TunnelValidation.ValidateRoutes(routes4, ipv6: false) is { } r4Error)
        {
            return (r4Error, RoutesBox);
        }

        var routes6 = TunnelValidation.SplitList(Routes6Box.Text);
        if (TunnelValidation.ValidateRoutes(routes6, ipv6: true) is { } r6Error)
        {
            return (r6Error, Routes6Box);
        }

        return (null, null);
    }
}
