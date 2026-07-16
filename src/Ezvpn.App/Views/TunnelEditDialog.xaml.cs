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

    /// <summary>Prefill the form from an existing profile + its stored token.</summary>
    public void LoadFrom(TunnelProfile profile, string? token)
    {
        NameBox.Text = profile.Name;
        NodeIdBox.Text = profile.ServerNodeId;
        TokenBox.Password = token ?? "";
        RelayBox.Text = string.Join(Environment.NewLine, profile.RelayUrls);
        RoutesBox.Text = string.Join(Environment.NewLine, profile.Routes);
        Routes6Box.Text = string.Join(Environment.NewLine, profile.Routes6);
        DnsBox.Text = profile.DnsServer ?? "";
        AutoReconnectCheck.IsChecked = profile.AutoReconnect;
        MaxAttemptsBox.Value = profile.MaxReconnectAttempts ?? double.NaN;
    }

    /// <summary>Build a brand-new profile and its token from the form (for Add).</summary>
    public (TunnelProfile Profile, string? Token) BuildResult()
    {
        var profile = new TunnelProfile();
        var token = ApplyTo(profile);
        return (profile, token);
    }

    /// <summary>
    /// Write the form into <paramref name="profile"/> and return the token text
    /// (empty string means "clear the stored token").
    /// </summary>
    public string ApplyTo(TunnelProfile profile)
    {
        profile.Name = NameBox.Text.Trim();
        profile.ServerNodeId = NodeIdBox.Text.Trim();
        profile.RelayUrls = TunnelValidation.SplitList(RelayBox.Text);
        profile.Routes = TunnelValidation.SplitList(RoutesBox.Text);
        profile.Routes6 = TunnelValidation.SplitList(Routes6Box.Text);
        var dns = DnsBox.Text.Trim();
        profile.DnsServer = dns.Length == 0 ? null : dns;
        profile.AutoReconnect = AutoReconnectCheck.IsChecked ?? true;
        profile.MaxReconnectAttempts =
            double.IsNaN(MaxAttemptsBox.Value) || MaxAttemptsBox.Value < 1
                ? null
                : (uint)MaxAttemptsBox.Value;
        return TokenBox.Password;
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
