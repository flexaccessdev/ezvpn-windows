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
          "mtu":1280,"gso_negotiated":false,
          "routes":["10.0.0.1/32"],"routes6":["fd00::1/128"],
          "connection":"Direct 1.2.3.4:52186 (rtt 1ms)","bypass_addrs":[]
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
        Assert.Equal("Direct 1.2.3.4:52186 (rtt 1ms)", status.Connection);
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
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    public void Parse_InvalidReturnsNull(string? input)
    {
        Assert.Null(ClientStatus.Parse(input));
    }
}
