using Ezvpn.Core;
using Xunit;

namespace Ezvpn.Core.Tests;

public class CredentialAuthKeyStoreTests
{
    [Fact]
    public void Records_RoundTripAsOneCredentialPerKey()
    {
        var secrets = new MemorySecretBackend();
        var records = new CredentialAuthKeyStore(secrets);

        records.Save(new StoredAuthKey("a", "laptop", "ed25519-sec:AAAA"));
        records.Save(new StoredAuthKey("b", "desktop", "ed25519-sec:BBBB"));
        records.Save(new StoredAuthKey("a", "renamed", "ed25519-sec:AAAA"));

        Assert.Equal(
            new[] { new StoredAuthKey("a", "renamed", "ed25519-sec:AAAA"), new StoredAuthKey("b", "desktop", "ed25519-sec:BBBB") },
            records.LoadAll().OrderBy(r => r.Id));
        Assert.Equal(
            """{"name":"renamed","secret":"ed25519-sec:AAAA"}""",
            secrets.Read(CredentialAuthKeyStore.KeyRecordPrefix + "a"));

        records.Delete("a");
        Assert.Equal("b", Assert.Single(records.LoadAll()).Id);
    }

    [Fact]
    public void Load_SkipsUnreadableRecordsAndIgnoresOtherTargets()
    {
        var secrets = new MemorySecretBackend();
        secrets.Write("ezvpn-key:good", """{"name":"laptop","secret":"ed25519-sec:AAAA"}""");
        secrets.Write("ezvpn-key:nameless", """{"secret":"ed25519-sec:BBBB"}""");
        secrets.Write("ezvpn-key:not-json", "garbage");
        secrets.Write("ezvpn-key:no-secret", """{"name":"x","secret":null}""");
        secrets.Write("ezvpn-profile-key:0123", "ed25519-sec:CCCC");

        var loaded = new CredentialAuthKeyStore(secrets).LoadAll().OrderBy(r => r.Id).ToList();

        Assert.Equal(
            new[] { new StoredAuthKey("good", "laptop", "ed25519-sec:AAAA"), new StoredAuthKey("nameless", "", "ed25519-sec:BBBB") },
            loaded);
    }

    [Fact]
    public void MemoryBackend_MatchesTargetsCaseInsensitively_LikeCredentialManager()
    {
        var secrets = new MemorySecretBackend();
        secrets.Write("EZVPN-KEY:A", "first");
        secrets.Write("ezvpn-key:a", "second");

        Assert.Equal("second", secrets.Read("Ezvpn-Key:A"));
        Assert.Equal(("A", "second"), Assert.Single(secrets.Enumerate("ezvpn-key:")));

        secrets.Delete("ezvpn-KEY:a");
        Assert.Null(secrets.Read("ezvpn-key:a"));
        Assert.Empty(secrets.Enumerate("ezvpn-key:"));
        // Deleting what is already gone is a no-op, as in Credential Manager.
        secrets.Delete("ezvpn-key:a");
    }
}
