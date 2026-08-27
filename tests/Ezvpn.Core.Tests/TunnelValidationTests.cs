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
    public void ValidateAuthKeyId_IsRequired()
    {
        Assert.NotNull(TunnelValidation.ValidateAuthKeyId(null));
        Assert.NotNull(TunnelValidation.ValidateAuthKeyId(""));
        Assert.NotNull(TunnelValidation.ValidateAuthKeyId("   "));
        Assert.Null(TunnelValidation.ValidateAuthKeyId("6b1c9d0e"));
    }

    [Fact]
    public void ValidateName_ReportsTheSubjectItWasGiven()
    {
        Assert.Contains(
            "A key with this name already exists.",
            TunnelValidation.ValidateName("laptop", new[] { "Laptop" }, subject: "key"));
        Assert.Contains(
            "A profile with this name already exists.",
            TunnelValidation.ValidateName("work", new[] { "work" }));
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
    public void SplitList_SplitsOnCommaOnlyAndTrims()
    {
        // Comma is the only separator; surrounding spaces/newlines are trimmed.
        var list = TunnelValidation.SplitList("10.0.0.0/8, 192.168.0.0/16 ,\n172.16.0.0/12");
        Assert.Equal(new[] { "10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12" }, list);
        Assert.Empty(TunnelValidation.SplitList(""));
        Assert.Empty(TunnelValidation.SplitList(null));
        Assert.Empty(TunnelValidation.SplitList("  ,  , "));
    }

    [Fact]
    public void SplitList_MatchesMacCsvSemantics()
    {
        // Mirrors ezvpn-apple's splitCSV test verbatim: comma-delimited, empty
        // fields dropped, each entry trimmed of whitespace/newlines. Guarantees a
        // profile's relay/route list parses identically on Windows and mac/linux.
        var list = TunnelValidation.SplitList(" https://relay.one , ,\nhttps://relay.two ");
        Assert.Equal(new[] { "https://relay.one", "https://relay.two" }, list);
    }
}
