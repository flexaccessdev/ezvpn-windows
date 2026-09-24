using Ezvpn.Core;
using Xunit;

namespace Ezvpn.Core.Tests;

public class ClientStatusTests
{
    [Fact]
    public void Parse_ConnectedDualStack()
    {
        const string json = """
        {
          "role":"client","instance":"default","state":"connected",
          "server_node_id":"node","device_id":"00000000deadbeef",
          "connected_since_secs":42,"mode":"dual-stack",
          "assigned_ip":"10.0.0.2","network":"10.0.0.1/32","gateway":"10.0.0.1",
          "assigned_ip6":"fd00::2","network6":"fd00::1/128","gateway6":"fd00::1",
          "mtu":1280,
          "routes":["10.0.0.1/32"],"routes6":["fd00::1/128"],
          "connection":null,"custom_relays":[],
          "bypass_addrs":[]
        }
        """;

        var status = ClientStatus.Parse(json);
        Assert.NotNull(status);
        Assert.True(status!.IsConnected);
        Assert.Equal("dual-stack", status.Mode);
        Assert.Equal("10.0.0.2", status.AssignedIp);
        Assert.Equal("fd00::1", status.Gateway6);
        Assert.Equal(1280, status.Mtu);
        Assert.Equal(42ul, status.ConnectedSinceSecs);
        Assert.Contains("10.0.0.1/32", status.Routes);
        Assert.Equal(0, status.FailedAttempts);
        Assert.Null(status.NextAttemptSecs);
        Assert.Null(status.LastError);
    }

    // The reconnect loop's progress while down: consecutive failures, when it
    // tries again (0 while an attempt is in progress), and the last error.
    [Fact]
    public void Parse_ReconnectingReportsProgress()
    {
        const string json = """
        {"role":"client","instance":"work","state":"disconnected","mode":"none",
         "server_node_id":"node","device_id":"x",
         "failed_attempts":3,"next_attempt_secs":8,
         "last_error":"Signaling error: Failed to connect to server"}
        """;
        var status = ClientStatus.Parse(json);
        Assert.NotNull(status);
        Assert.False(status!.IsConnected);
        Assert.Equal(3, status.FailedAttempts);
        Assert.Equal(8ul, status.NextAttemptSecs);
        Assert.Equal("Signaling error: Failed to connect to server", status.LastError);

        const string trying = """
        {"state":"disconnected","failed_attempts":1,"next_attempt_secs":0,"last_error":"Connection lost"}
        """;
        var mid = ClientStatus.Parse(trying);
        Assert.Equal(1, mid!.FailedAttempts);
        Assert.Equal(0ul, mid.NextAttemptSecs);
    }

    [Fact]
    public void Parse_Disconnected()
    {
        const string json = """
        {"role":"client","instance":"work","state":"disconnected","mode":"none",
         "server_node_id":"node","device_id":"x"}
        """;
        var status = ClientStatus.Parse(json);
        Assert.NotNull(status);
        Assert.False(status!.IsConnected);
        Assert.Null(status.AssignedIp);
        Assert.Empty(status.Routes);
        Assert.Equal(0, status.FailedAttempts);
        Assert.Null(status.NextAttemptSecs);
        Assert.Null(status.LastError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("123")]      // valid JSON, but not an object
    [InlineData("[1,2,3]")]  // valid JSON array, not an object
    public void Parse_InvalidReturnsNull(string? input)
    {
        Assert.Null(ClientStatus.Parse(input));
    }

    // Structural immunity: unknown keys the core may add are ignored, and
    // consumed keys still decode. The decoder models what the app reads, not the
    // full core struct, so status-shape drift never breaks parsing.
    [Fact]
    public void Parse_IgnoresUnknownKeys()
    {
        const string json = """
        {"state":"connected","mode":"ipv4","assigned_ip":"10.0.0.2",
         "gso_negotiated":true,"some_future_field":{"nested":42},"extra":[1,2,3]}
        """;
        var status = ClientStatus.Parse(json);
        Assert.NotNull(status);
        Assert.True(status!.IsConnected);
        Assert.Equal("ipv4", status.Mode);
        Assert.Equal("10.0.0.2", status.AssignedIp);
    }

    // Graceful degradation: a consumed key present with an unexpected type (or a
    // JSON null) reads as null/empty rather than throwing.
    [Fact]
    public void Parse_WrongTypedValuesDegradeGracefully()
    {
        const string json = """
        {"state":"connected","mtu":"not-a-number","connected_since_secs":null,
         "assigned_ip":123,"routes":"not-an-array"}
        """;
        var status = ClientStatus.Parse(json);
        Assert.NotNull(status);
        Assert.Null(status!.Mtu);
        Assert.Null(status.ConnectedSinceSecs);
        Assert.Null(status.AssignedIp);
        Assert.Empty(status.Routes);
    }
}
