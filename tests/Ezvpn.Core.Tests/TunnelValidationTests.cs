using Ezvpn.Core;
using Xunit;

namespace Ezvpn.Core.Tests;

public class TunnelValidationTests
{
    [Fact]
    public void ValidateName_RejectsEmptyAndDuplicates()
    {
        Assert.NotNull(TunnelValidation.ValidateName(""));
        Assert.NotNull(TunnelValidation.ValidateName("   "));
        Assert.Null(TunnelValidation.ValidateName("work"));
        Assert.NotNull(TunnelValidation.ValidateName("work", new[] { "Work" }));
        Assert.Null(TunnelValidation.ValidateName("home", new[] { "work" }));
    }

    [Fact]
    public void ValidateServerNodeId_RejectsEmptyAndSpaces()
    {
        Assert.NotNull(TunnelValidation.ValidateServerNodeId(""));
        Assert.NotNull(TunnelValidation.ValidateServerNodeId("has space"));
        Assert.Null(TunnelValidation.ValidateServerNodeId("k51qzi5uqu5d"));
    }

    [Fact]
    public void ValidateAuthToken_IsRequired()
    {
        Assert.NotNull(TunnelValidation.ValidateAuthToken(null));
        Assert.NotNull(TunnelValidation.ValidateAuthToken(""));
        Assert.NotNull(TunnelValidation.ValidateAuthToken("   "));
        Assert.Null(TunnelValidation.ValidateAuthToken("v0123456789"));
    }

    [Theory]
    [InlineData("10.0.0.0/8", false, true)]
    [InlineData("0.0.0.0/0", false, true)]
    [InlineData("fd00::/8", true, true)]
    [InlineData("::/0", true, true)]
    [InlineData("10.0.0.0/8", true, false)]   // v4 offered as v6
    [InlineData("fd00::/8", false, false)]    // v6 offered as v4
    [InlineData("not-a-cidr", false, false)]
    [InlineData("10.0.0.0", false, false)]    // missing prefix len
    public void ValidateCidr(string cidr, bool ipv6, bool expectValid)
    {
        var err = TunnelValidation.ValidateCidr(cidr, ipv6);
        Assert.Equal(expectValid, err is null);
    }

    [Fact]
    public void SplitList_HandlesMixedSeparators()
    {
        var list = TunnelValidation.SplitList("10.0.0.0/8, 192.168.0.0/16\n172.16.0.0/12");
        Assert.Equal(new[] { "10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12" }, list);
        Assert.Empty(TunnelValidation.SplitList(""));
        Assert.Empty(TunnelValidation.SplitList(null));
    }
}
