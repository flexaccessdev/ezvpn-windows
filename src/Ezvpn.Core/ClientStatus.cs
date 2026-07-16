using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ezvpn.Core;

/// <summary>
/// Deserialized form of the JSON returned by <c>ezvpn_status</c> — the client
/// variant of the Rust <c>StatusSnapshot</c> (see <c>src/control.rs</c>). The
/// enum is internally tagged, so the JSON is flat: <c>role</c> plus the
/// <c>ClientStatus</c> fields (snake_case).
/// </summary>
public sealed class ClientStatus
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("instance")]
    public string Instance { get; set; } = "";

    /// <summary>"connected" once the handshake succeeds, otherwise "disconnected".</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "disconnected";

    [JsonPropertyName("server_node_id")]
    public string ServerNodeId { get; set; } = "";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("connected_since_secs")]
    public ulong? ConnectedSinceSecs { get; set; }

    /// <summary>"ipv4", "ipv6", "dual-stack", or "none".</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "none";

    [JsonPropertyName("assigned_ip")]
    public string? AssignedIp { get; set; }

    [JsonPropertyName("network")]
    public string? Network { get; set; }

    [JsonPropertyName("gateway")]
    public string? Gateway { get; set; }

    [JsonPropertyName("assigned_ip6")]
    public string? AssignedIp6 { get; set; }

    [JsonPropertyName("network6")]
    public string? Network6 { get; set; }

    [JsonPropertyName("gateway6")]
    public string? Gateway6 { get; set; }

    [JsonPropertyName("mtu")]
    public int? Mtu { get; set; }

    [JsonPropertyName("gso_negotiated")]
    public bool? GsoNegotiated { get; set; }

    [JsonPropertyName("routes")]
    public List<string> Routes { get; set; } = new();

    [JsonPropertyName("routes6")]
    public List<string> Routes6 { get; set; } = new();

    /// <summary>Live iroh path description (direct/relay, rtt), when connected.</summary>
    [JsonPropertyName("connection")]
    public string? Connection { get; set; }

    [JsonPropertyName("bypass_addrs")]
    public List<string> BypassAddrs { get; set; } = new();

    [JsonPropertyName("log_file")]
    public string? LogFile { get; set; }

    [JsonIgnore]
    public bool IsConnected => string.Equals(State, "connected", StringComparison.Ordinal);

    /// <summary>
    /// Parse a status JSON string. Returns null on empty/invalid input (e.g. a
    /// null handle wrote an empty string).
    /// </summary>
    public static ClientStatus? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ClientStatus>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
