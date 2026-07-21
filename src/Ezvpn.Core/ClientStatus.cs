using System.Text.Json;

namespace Ezvpn.Core;

/// <summary>
/// A typed, read-only view of the JSON returned by <c>ezvpn_status</c> (the
/// client variant of the Rust <c>StatusSnapshot</c> in <c>../ezvpn/src/control.rs</c>).
///
/// This is deliberately <b>not</b> a 1:1 mirror of the Rust struct. Rather than
/// declaring a property for every field the core emits, it decodes the raw JSON
/// lazily and exposes <b>only the keys this app actually consumes</b>. That makes
/// it structurally immune to status-shape drift in the core:
///   * a field the core <i>adds</i> is ignored until something here reads it;
///   * a field the core <i>removes</i> or renames just makes its accessor return
///     null/empty — nothing dead is left mirroring it, and parsing never breaks.
/// (Mirrors how <c>ezvpn-apple</c>'s <c>TunnelSnapshotDecoder</c> reads only the
/// keys it needs.) To surface a new field, add an accessor for its key here.
/// </summary>
public sealed class ClientStatus
{
    private readonly JsonElement _root;

    private ClientStatus(JsonElement root) => _root = root;

    // --- Consumed fields (add an accessor here to surface a new key) ----------

    /// <summary>"connected" once the handshake succeeds, otherwise "disconnected".</summary>
    public string State => Str("state") ?? "disconnected";

    /// <summary>"ipv4", "ipv6", "dual-stack", or "none".</summary>
    public string Mode => Str("mode") ?? "none";

    public string? AssignedIp => Str("assigned_ip");

    public string? AssignedIp6 => Str("assigned_ip6");

    public string? Gateway => Str("gateway");

    public string? Gateway6 => Str("gateway6");

    public int? Mtu => Int("mtu");

    public ulong? ConnectedSinceSecs => ULong("connected_since_secs");

    public IReadOnlyList<string> Routes => StrList("routes");

    public IReadOnlyList<string> Routes6 => StrList("routes6");

    /// <summary>Live iroh path description (direct/relay, rtt), when connected.</summary>
    public string? Connection => Str("connection");

    public IReadOnlyList<string> BypassAddrs => StrList("bypass_addrs");

    public IReadOnlyList<CustomRelayStatus> CustomRelays
    {
        get
        {
            var relays = new List<CustomRelayStatus>();
            if (_root.TryGetProperty("custom_relays", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object)
                    {
                        relays.Add(new CustomRelayStatus(e));
                    }
                }
            }
            return relays;
        }
    }

    public bool IsConnected => string.Equals(State, "connected", StringComparison.Ordinal);

    // --- Parsing & typed key accessors ----------------------------------------

    /// <summary>
    /// Parse a status JSON string. Returns null on empty/invalid input (e.g. a
    /// null handle wrote an empty string) or a non-object top-level value.
    /// </summary>
    public static ClientStatus? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            // Clone so the element outlives the disposed JsonDocument.
            return new ClientStatus(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? Str(string key) =>
        _root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private int? Int(string key) =>
        _root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    private ulong? ULong(string key) =>
        _root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var n)
            ? n
            : null;

    private List<string> StrList(string key)
    {
        var list = new List<string>();
        if (_root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s)
                {
                    list.Add(s);
                }
            }
        }
        return list;
    }
}

/// <summary>A configured custom relay URL and its latest endpoint health.</summary>
public sealed class CustomRelayStatus
{
    internal CustomRelayStatus(JsonElement e)
    {
        Url = e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString() ?? ""
            : "";
        Working = e.TryGetProperty("working", out var w)
            ? w.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null,
            }
            : null;
        Error = e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
            ? err.GetString()
            : null;
    }

    public string Url { get; }

    /// <summary>True/false when iroh has observed health; null while unavailable.</summary>
    public bool? Working { get; }

    public string? Error { get; }
}
