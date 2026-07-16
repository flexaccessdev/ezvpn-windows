using System.Text.Json.Serialization;

namespace Ezvpn.Core;

/// <summary>
/// A saved VPN connection profile. Mirrors the Apple app's <c>TunnelProfile</c>.
/// There is no server IP/port/hostname — the "server address" is an iroh
/// <see cref="ServerNodeId"/> (endpoint id). The auth token is the one secret
/// and is NOT stored here; it lives in Windows Credential Manager keyed by
/// <see cref="Id"/> (see <c>TokenStore</c>).
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

    /// <summary>Optional relay URL hints. When empty, iroh uses its default relay map.</summary>
    [JsonPropertyName("relayUrls")]
    public List<string> RelayUrls { get; set; } = new();

    /// <summary>Optional peer-discovery DNS server URL, or "none" to disable discovery.</summary>
    [JsonPropertyName("dnsServer")]
    public string? DnsServer { get; set; }

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
