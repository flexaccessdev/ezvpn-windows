using System.Text.Json;

namespace Ezvpn.Core;

/// <summary>
/// A typed view of the JSON returned by <c>ezvpn_conn_path</c>: a point-in-time
/// snapshot of how the running tunnel reaches the server — every iroh path with
/// the one it routes over marked, plus each custom relay's health. Taken on
/// demand (the "Connection path…" dialog), like the Apple and Android apps,
/// because the relay health check is an HTTP request per relay.
/// </summary>
public sealed class ConnPathSnapshot
{
    private ConnPathSnapshot(IReadOnlyList<ConnPath> paths, IReadOnlyList<CustomRelayStatus> customRelays)
    {
        Paths = paths;
        CustomRelays = customRelays;
    }

    /// <summary>Every discovered path; empty while the tunnel is down.</summary>
    public IReadOnlyList<ConnPath> Paths { get; }

    /// <summary>Configured custom relays and their health; empty with the default relays.</summary>
    public IReadOnlyList<CustomRelayStatus> CustomRelays { get; }

    /// <summary>
    /// Parse a conn-path JSON string. Returns null on empty/invalid input (e.g. a
    /// null handle wrote an empty string) or a non-object top-level value.
    /// </summary>
    public static ConnPathSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var paths = new List<ConnPath>();
            if (root.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object)
                    {
                        paths.Add(new ConnPath(e));
                    }
                }
            }
            var relays = new List<CustomRelayStatus>();
            if (root.TryGetProperty("custom_relays", out var rel) && rel.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in rel.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object)
                    {
                        relays.Add(new CustomRelayStatus(e));
                    }
                }
            }
            return new ConnPathSnapshot(paths, relays);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One iroh path to the server.</summary>
public sealed class ConnPath
{
    internal ConnPath(JsonElement e)
    {
        Kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString() ?? "other"
            : "other";
        Display = e.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? ""
            : "";
        Selected = e.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.True;
    }

    /// <summary>"direct", "relay", or "other" (forward-compatible catch-all).</summary>
    public string Kind { get; }

    /// <summary>Human line, e.g. <c>Direct 1.2.3.4:52186 (rtt 1ms)</c>.</summary>
    public string Display { get; }

    /// <summary>Whether iroh routes traffic over this path right now.</summary>
    public bool Selected { get; }
}

/// <summary>A configured custom relay URL and its <c>/healthz</c> result.</summary>
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

    /// <summary>True on a 2xx, false when unreachable/non-2xx, null if the check could not run.</summary>
    public bool? Working { get; }

    public string? Error { get; }
}
