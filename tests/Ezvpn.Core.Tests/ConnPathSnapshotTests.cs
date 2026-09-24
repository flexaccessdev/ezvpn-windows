using Ezvpn.Core;
using Xunit;

namespace Ezvpn.Core.Tests;

public class ConnPathSnapshotTests
{
    [Fact]
    public void Parse_PathsAndRelays()
    {
        const string json = """
        {"paths":[
           {"kind":"direct","display":"Direct 1.2.3.4:52186 (rtt 1ms)","selected":true},
           {"kind":"relay","display":"Relay https://relay.example/ (rtt 42ms)","selected":false}],
         "custom_relays":[
           {"url":"https://relay.example/","working":true,"error":null},
           {"url":"https://relay2.example/","working":false,"error":"timed out"},
           {"url":"https://relay3.example/","working":null,"error":null}]}
        """;
        var snap = ConnPathSnapshot.Parse(json);
        Assert.NotNull(snap);
        Assert.Equal(2, snap!.Paths.Count);
        Assert.Equal("direct", snap.Paths[0].Kind);
        Assert.Equal("Direct 1.2.3.4:52186 (rtt 1ms)", snap.Paths[0].Display);
        Assert.True(snap.Paths[0].Selected);
        Assert.Equal("relay", snap.Paths[1].Kind);
        Assert.False(snap.Paths[1].Selected);
        Assert.Equal(3, snap.CustomRelays.Count);
        Assert.True(snap.CustomRelays[0].Working);
        Assert.False(snap.CustomRelays[1].Working);
        Assert.Equal("timed out", snap.CustomRelays[1].Error);
        Assert.Null(snap.CustomRelays[2].Working);
    }

    // Disconnected: both arrays empty, still a valid snapshot.
    [Fact]
    public void Parse_Empty()
    {
        var snap = ConnPathSnapshot.Parse("""{"paths":[],"custom_relays":[]}""");
        Assert.NotNull(snap);
        Assert.Empty(snap!.Paths);
        Assert.Empty(snap.CustomRelays);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Parse_InvalidReturnsNull(string? json) => Assert.Null(ConnPathSnapshot.Parse(json));

    [Fact]
    public void Parse_ToleratesWrongTypes()
    {
        var snap = ConnPathSnapshot.Parse("""{"paths":[{"kind":1,"selected":"yes"},"x"],"custom_relays":"nope"}""");
        Assert.NotNull(snap);
        Assert.Single(snap!.Paths);
        Assert.Equal("other", snap.Paths[0].Kind);
        Assert.Equal("", snap.Paths[0].Display);
        Assert.False(snap.Paths[0].Selected);
        Assert.Empty(snap.CustomRelays);
    }
}
