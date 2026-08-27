using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ezvpn.Core;

/// <summary>
/// Builds the JSON config string passed to <c>ezvpn_start</c> (the shape defined
/// in <c>windows/ezvpn.h</c> / <c>src/ffi_windows.rs</c>). Kept separate from
/// <see cref="TunnelProfile"/> so the FFI wire shape is explicit and testable.
/// </summary>
public static class EzvpnConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Serialize <paramref name="profile"/> plus its secrets into the
    /// <c>ezvpn_start</c> config JSON. <paramref name="authKey"/> is the client's
    /// ed25519 secret key (<c>ed25519-sec:…</c>), whose public half must be on
    /// the server's authorized-keys file; it is required, and a null/blank key
    /// throws <see cref="ArgumentException"/>.
    ///
    /// <paramref name="relayAuthToken"/> is the optional shared relay bearer
    /// token. It is only valid with custom relays: a non-blank token with no
    /// <see cref="TunnelProfile.RelayUrls"/> throws (the core rejects it too),
    /// and a null/blank token is omitted from the JSON.
    /// </summary>
    public static string Build(TunnelProfile profile, string? authKey, string? relayAuthToken = null)
    {
        if (string.IsNullOrWhiteSpace(authKey))
        {
            throw new ArgumentException("An auth key is required.", nameof(authKey));
        }

        var relayToken = string.IsNullOrWhiteSpace(relayAuthToken) ? null : relayAuthToken;
        if (relayToken is not null && profile.RelayUrls.Count == 0)
        {
            throw new ArgumentException(
                "A relay token requires at least one relay URL.", nameof(relayAuthToken));
        }

        var dto = new StartConfigDto
        {
            ServerNodeId = profile.ServerNodeId,
            AuthKey = authKey,
            RelayUrls = profile.RelayUrls,
            RelayAuthToken = relayToken,
            Routes = profile.Routes,
            Routes6 = profile.Routes6,
            Instance = profile.Instance,
            AutoReconnect = profile.AutoReconnect,
            MaxReconnectAttempts = profile.MaxReconnectAttempts,
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    private sealed class StartConfigDto
    {
        [JsonPropertyName("server_node_id")]
        public string ServerNodeId { get; set; } = "";

        [JsonPropertyName("auth_key")]
        public string? AuthKey { get; set; }

        [JsonPropertyName("relay_urls")]
        public List<string> RelayUrls { get; set; } = new();

        [JsonPropertyName("relay_auth_token")]
        public string? RelayAuthToken { get; set; }

        [JsonPropertyName("routes")]
        public List<string> Routes { get; set; } = new();

        [JsonPropertyName("routes6")]
        public List<string> Routes6 { get; set; } = new();

        [JsonPropertyName("instance")]
        public string Instance { get; set; } = "default";

        [JsonPropertyName("auto_reconnect")]
        public bool AutoReconnect { get; set; } = true;

        [JsonPropertyName("max_reconnect_attempts")]
        public uint? MaxReconnectAttempts { get; set; }
    }
}
