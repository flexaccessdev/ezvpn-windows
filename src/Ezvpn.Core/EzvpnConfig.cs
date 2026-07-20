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
    /// Serialize <paramref name="profile"/> plus its <paramref name="authToken"/>
    /// into the <c>ezvpn_start</c> config JSON. The auth token is required; a
    /// null/blank token throws <see cref="ArgumentException"/>.
    /// </summary>
    public static string Build(TunnelProfile profile, string? authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken))
        {
            throw new ArgumentException("An auth token is required.", nameof(authToken));
        }

        var dto = new StartConfigDto
        {
            ServerNodeId = profile.ServerNodeId,
            AuthToken = authToken,
            RelayUrls = profile.RelayUrls,
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

        [JsonPropertyName("auth_token")]
        public string? AuthToken { get; set; }

        [JsonPropertyName("relay_urls")]
        public List<string> RelayUrls { get; set; } = new();

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
