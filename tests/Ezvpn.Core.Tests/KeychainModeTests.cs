using Ezvpn.Core;
using Xunit;

namespace Ezvpn.Core.Tests;

public class KeychainModeTests
{
    [Fact]
    public void Unset_IsCredentialManager()
    {
        Assert.False(KeychainMode.IsMock(null));
    }

    [Fact]
    public void Mock_IsAskedForByNameOnly()
    {
        Assert.True(KeychainMode.IsMock("mock"));
        // A typo is not the real credential store.
        foreach (var wrong in new[] { "", "Mock", "memory", "1" })
        {
            var ex = Assert.Throws<ArgumentException>(() => KeychainMode.IsMock(wrong));
            Assert.Contains("the only value it takes", ex.Message);
        }
    }
}
