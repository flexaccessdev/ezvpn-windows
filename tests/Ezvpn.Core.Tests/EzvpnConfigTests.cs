using System.Text.Json;
using Ezvpn.Core;
using Xunit;

namespace Ezvpn.Core.Tests;

public class EzvpnConfigTests
{
    [Fact]
    public void Build_EmitsSnakeCaseKeys_AndOmitsNulls()
    {
        var profile = new TunnelProfile
        {
            Name = "work",
            ServerNodeId = "abc123",
            Routes = { "10.0.0.0/8" },
            Routes6 = { "fd00::/8" },
            RelayUrls = { "https://relay.example/" },
            AutoReconnect = true,
        };

        var json = EzvpnConfig.Build(profile, "tok");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("abc123", root.GetProperty("server_node_id").GetString());
        Assert.Equal("10.0.0.0/8", root.GetProperty("routes")[0].GetString());
        Assert.Equal("fd00::/8", root.GetProperty("routes6")[0].GetString());
        Assert.True(root.GetProperty("auto_reconnect").GetBoolean());
        // relay_only was removed from the core FFI config; it must not be emitted.
        Assert.False(root.TryGetProperty("relay_only", out _));
        Assert.Equal(profile.Instance, root.GetProperty("instance").GetString());
        // Null max attempts is omitted, not emitted as null.
        Assert.False(root.TryGetProperty("max_reconnect_attempts", out _));
        // No relay token was supplied, so the key is omitted.
        Assert.False(root.TryGetProperty("relay_auth_token", out _));
    }

    [Fact]
    public void Build_EmitsRelayAuthToken_WithCustomRelays()
    {
        var profile = new TunnelProfile
        {
            ServerNodeId = "node",
            RelayUrls = { "https://relay.example/" },
        };

        var json = EzvpnConfig.Build(profile, "tok", "relay-secret");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("relay-secret", doc.RootElement.GetProperty("relay_auth_token").GetString());
    }

    [Fact]
    public void Build_OmitsBlankRelayAuthToken()
    {
        var profile = new TunnelProfile
        {
            ServerNodeId = "node",
            RelayUrls = { "https://relay.example/" },
        };

        var json = EzvpnConfig.Build(profile, "tok", "   ");
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("relay_auth_token", out _));
    }

    [Fact]
    public void Build_RelayAuthTokenWithoutRelays_Throws()
    {
        var profile = new TunnelProfile { ServerNodeId = "node" };
        Assert.Throws<ArgumentException>(() => EzvpnConfig.Build(profile, "tok", "relay-secret"));
    }

    [Fact]
    public void Build_IncludesTokenAndMaxAttempts_WhenSet()
    {
        var profile = new TunnelProfile
        {
            ServerNodeId = "node",
            MaxReconnectAttempts = 5,
        };

        var json = EzvpnConfig.Build(profile, "v0123456789");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("v0123456789", root.GetProperty("auth_token").GetString());
        Assert.Equal(5u, root.GetProperty("max_reconnect_attempts").GetUInt32());
    }

    [Fact]
    public void Build_BlankOrNullToken_Throws()
    {
        var profile = new TunnelProfile { ServerNodeId = "node" };
        Assert.Throws<ArgumentException>(() => EzvpnConfig.Build(profile, null));
        Assert.Throws<ArgumentException>(() => EzvpnConfig.Build(profile, ""));
        Assert.Throws<ArgumentException>(() => EzvpnConfig.Build(profile, "   "));
    }

    [Fact]
    public void Instance_IsStableAndAsciiSafe()
    {
        var profile = new TunnelProfile();
        Assert.StartsWith("gui_", profile.Instance);
        Assert.All(profile.Instance, c => Assert.True(char.IsLetterOrDigit(c) || c == '_'));
        Assert.Equal(profile.Instance, profile.Instance); // stable
    }
}
