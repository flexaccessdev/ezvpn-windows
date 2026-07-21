using Ezvpn.Core;
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

    /// <summary>Names to reject as duplicates (exclude the profile being edited).</summary>
    public void SetExistingNames(IEnumerable<string> names) => _existingNames = names.ToArray();

    /// <summary>Prefill the form from an existing profile + its stored token(s).</summary>
    public void LoadFrom(TunnelProfile profile, string? token, string? relayToken)
    {
        NameBox.Text = profile.Name;
        NodeIdBox.Text = profile.ServerNodeId;
        TokenBox.Password = token ?? "";
        RelayBox.Text = string.Join(", ", profile.RelayUrls);
        RelayTokenBox.Password = relayToken ?? "";
        RoutesBox.Text = string.Join(Environment.NewLine, profile.Routes);
        Routes6Box.Text = string.Join(Environment.NewLine, profile.Routes6);
        AutoReconnectCheck.IsChecked = profile.AutoReconnect;
        MaxAttemptsBox.Value = profile.MaxReconnectAttempts ?? double.NaN;
        UpdateRelayTokenEnabled();
    }

    /// <summary>The optional relay token from the form, or null when blank.</summary>
    public string? RelayToken =>
        string.IsNullOrWhiteSpace(RelayTokenBox.Password) ? null : RelayTokenBox.Password;

    /// <summary>Build a brand-new profile and its token(s) from the form (for Add).</summary>
    public (TunnelProfile Profile, string Token, string? RelayToken) BuildResult()
    {
        var profile = new TunnelProfile();
        var token = ApplyTo(profile);
        return (profile, token, RelayToken);
    }

    /// <summary>
    /// Write the form into <paramref name="profile"/> and return the (required)
    /// auth token text. Validation guarantees it is non-blank before Save. The
    /// optional relay token is read separately via <see cref="RelayToken"/>.
    /// </summary>
    public string ApplyTo(TunnelProfile profile)
    {
        profile.Name = NameBox.Text.Trim();
        profile.ServerNodeId = NodeIdBox.Text.Trim();
        profile.RelayUrls = TunnelValidation.SplitList(RelayBox.Text);
        profile.Routes = TunnelValidation.SplitList(RoutesBox.Text);
        profile.Routes6 = TunnelValidation.SplitList(Routes6Box.Text);
        profile.AutoReconnect = AutoReconnectCheck.IsChecked ?? true;
        profile.MaxReconnectAttempts = ParseMaxAttempts(MaxAttemptsBox.Value);
        return TokenBox.Password;
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

        var tokenError = TunnelValidation.ValidateAuthToken(TokenBox.Password);
        if (tokenError is not null)
        {
            return tokenError;
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
