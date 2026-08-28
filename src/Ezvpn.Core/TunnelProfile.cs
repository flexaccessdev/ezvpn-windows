using System.Text.Json.Serialization;

namespace Ezvpn.Core;

/// <summary>
/// A saved VPN connection profile. Mirrors the Apple app's <c>TunnelProfile</c>.
/// There is no server IP/port/hostname — the "server address" is an iroh
/// <see cref="ServerNodeId"/> (endpoint id). No secret is stored here: the
/// client's ed25519 auth key and the optional relay bearer token live in Windows
/// Credential Manager keyed by <see cref="Id"/> (see <c>SecretStore</c>).
/// </summary>
public sealed class TunnelProfile
{
    /// <summary>Stable identifier, minted once when the profile is created.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name (must be unique among profiles).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The server's iroh endpoint id (node id).</summary>
    [JsonPropertyName("serverNodeId")]
    public string ServerNodeId { get; set; } = "";

    /// <summary>
    /// Which key from the app's shared auth-key list this profile authenticates
    /// with (<see cref="AuthKeyStore.Key.Id"/>). The key's <em>secret</em> is
    /// never here — it is copied into the profile's own credential on save (see
    /// <c>SecretStore</c>); this is only the non-secret reference the editor
    /// re-selects and the UI names the key by.
    ///
    /// <c>required</c>, with no default: a profile without a key cannot be built
    /// in code, and JSON that omits <c>authKeyId</c> fails to deserialize rather
    /// than loading as a keyless profile.
    /// </summary>
    [JsonPropertyName("authKeyId")]
    public required string AuthKeyId { get; set; }

    /// <summary>Optional relay URL hints. When empty, iroh uses its default relay map.</summary>
    [JsonPropertyName("relayUrls")]
    public List<string> RelayUrls { get; set; } = new();

    /// <summary>IPv4 CIDRs routed through the tunnel (split tunnel). Optional.</summary>
    [JsonPropertyName("routes")]
    public List<string> Routes { get; set; } = new();

    /// <summary>IPv6 CIDRs routed through the tunnel. Optional.</summary>
    [JsonPropertyName("routes6")]
    public List<string> Routes6 { get; set; } = new();

    /// <summary>Reconnect automatically on connection loss (default true).</summary>
    [JsonPropertyName("autoReconnect")]
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Cap on total reconnect attempts; null = unlimited.</summary>
    [JsonPropertyName("maxReconnectAttempts")]
    public uint? MaxReconnectAttempts { get; set; }

    /// <summary>
    /// The per-profile ezvpn "instance" name that scopes the single-instance
    /// lock in the Rust core. Derived from <see cref="Id"/> so distinct profiles
    /// never collide, and stable across edits. ASCII letters/digits/underscores
    /// only (see the Rust <c>validate_instance_name</c>).
    /// </summary>
    [JsonIgnore]
    public string Instance => "gui_" + Id.ToString("N");
}
