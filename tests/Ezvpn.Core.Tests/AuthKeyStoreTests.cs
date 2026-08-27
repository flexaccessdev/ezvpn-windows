using Ezvpn.Core;
using Xunit;

namespace Ezvpn.Core.Tests;

public class AuthKeyStoreTests
{
    /// <summary>
    /// In-memory stand-in for Credential Manager. <see cref="FailOn"/> makes one
    /// kind of write blow up, the way a Credential Manager failure would.
    /// </summary>
    private sealed class FakeRecordStore : IAuthKeyRecordStore
    {
        public Dictionary<string, StoredAuthKey> Records { get; } = new();
        public bool FailLoad { get; set; }
        public string? FailOn { get; set; } // "save" or "delete"

        public IReadOnlyList<StoredAuthKey> LoadAll() =>
            FailLoad
                ? throw new InvalidOperationException("CredEnumerate failed (error 5).")
                : Records.Values.ToList();

        public void Save(StoredAuthKey record)
        {
            if (FailOn == "save")
            {
                throw new InvalidOperationException("CredWrite failed (error 87).");
            }
            Records[record.Id] = record;
        }

        public void Delete(string id)
        {
            if (FailOn == "delete")
            {
                throw new InvalidOperationException("CredDelete failed (error 5).");
            }
            Records.Remove(id);
        }
    }

    // Public halves are derived, never stored: the fake mirrors the Rust core by
    // accepting only the "ed25519-sec:" form and deriving a matching public key.
    private static string? Derive(string secret) =>
        secret.StartsWith("ed25519-sec:", StringComparison.Ordinal)
            ? "ed25519-pub:" + secret["ed25519-sec:".Length..]
            : null;

    private static AuthKeyStore StoreOver(FakeRecordStore storage) =>
        new(storage, Derive);

    [Fact]
    public void Add_StoresTheRecordAndDerivesThePublicKey()
    {
        var storage = new FakeRecordStore();
        var store = StoreOver(storage);

        Assert.Null(store.Add("laptop", "  ed25519-sec:AAAA  ", out var added));
        Assert.NotNull(added);
        Assert.Equal("laptop", added!.Name);
        // The secret is trimmed and the public half derived, not persisted.
        Assert.Equal("ed25519-sec:AAAA", added.Secret);
        Assert.Equal("ed25519-pub:AAAA", added.PublicKey);
        Assert.Equal(added.Secret, Assert.Single(storage.Records).Value.Secret);
        Assert.Same(added, store.Find(added.Id));
    }

    [Fact]
    public void Add_RejectsBlankAndDuplicateNames()
    {
        var store = StoreOver(new FakeRecordStore());
        store.Add("laptop", "ed25519-sec:AAAA", out _);

        Assert.NotNull(store.Add("", "ed25519-sec:BBBB", out _));
        Assert.Contains("A key with this name", store.Add("Laptop", "ed25519-sec:BBBB", out _));
        Assert.Single(store.Keys);
    }

    [Fact]
    public void Add_RejectsAnUnparsableSecret()
    {
        var store = StoreOver(new FakeRecordStore());

        Assert.Contains("Not a valid secret key", store.Add("laptop", "hunter2", out var added));
        Assert.Null(added);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public void Add_RejectsTheSameKeypairTwice()
    {
        var store = StoreOver(new FakeRecordStore());
        store.Add("laptop", "ed25519-sec:AAAA", out _);

        Assert.Contains("already holds this secret", store.Add("desktop", "ed25519-sec:AAAA", out _));
        Assert.Single(store.Keys);
    }

    [Fact]
    public void Add_LeavesTheListUntouchedWhenTheWriteFails()
    {
        var storage = new FakeRecordStore { FailOn = "save" };
        var store = StoreOver(storage);

        Assert.Contains("CredWrite failed", store.Add("laptop", "ed25519-sec:AAAA", out var added));
        Assert.Null(added);
        Assert.Empty(store.Keys);
        Assert.Empty(storage.Records);
    }

    [Fact]
    public void Rename_ValidatesAndPersists()
    {
        var storage = new FakeRecordStore();
        var store = StoreOver(storage);
        store.Add("laptop", "ed25519-sec:AAAA", out var key);
        store.Add("desktop", "ed25519-sec:BBBB", out _);

        Assert.NotNull(store.Rename(key!.Id, "  Desktop "));       // duplicate
        Assert.NotNull(store.Rename(key.Id, "   "));               // blank
        Assert.Null(store.Rename(key.Id, "  work laptop "));

        Assert.Equal("work laptop", store.Find(key.Id)!.Name);
        Assert.Equal("work laptop", storage.Records[key.Id].Name);
        // The secret rides along unchanged.
        Assert.Equal("ed25519-sec:AAAA", storage.Records[key.Id].Secret);
    }

    [Fact]
    public void Rename_KeepsTheStoredNameWhenTheWriteFails()
    {
        var storage = new FakeRecordStore();
        var store = StoreOver(storage);
        store.Add("laptop", "ed25519-sec:AAAA", out var key);

        storage.FailOn = "save";
        Assert.Contains("CredWrite failed", store.Rename(key!.Id, "renamed"));
        Assert.Equal("laptop", store.Find(key.Id)!.Name);
    }

    [Fact]
    public void Delete_RemovesTheRecord_AndKeepsItWhenTheWriteFails()
    {
        var storage = new FakeRecordStore();
        var store = StoreOver(storage);
        store.Add("laptop", "ed25519-sec:AAAA", out var key);

        storage.FailOn = "delete";
        Assert.Contains("CredDelete failed", store.Delete(key!.Id));
        Assert.Single(store.Keys);

        storage.FailOn = null;
        Assert.Null(store.Delete(key.Id));
        Assert.Empty(store.Keys);
        Assert.Empty(storage.Records);
        // Deleting what is already gone is a no-op, not an error.
        Assert.Null(store.Delete(key.Id));
    }

    [Fact]
    public void Keys_AreOrderedByNameAndStayOrderedThroughEdits()
    {
        var store = StoreOver(new FakeRecordStore());
        store.Add("zeta", "ed25519-sec:AAAA", out _);
        store.Add("alpha", "ed25519-sec:BBBB", out var alpha);
        store.Add("Mid", "ed25519-sec:CCCC", out _);

        Assert.Equal(new[] { "alpha", "Mid", "zeta" }, store.Keys.Select(k => k.Name));

        store.Rename(alpha!.Id, "omega");
        Assert.Equal(new[] { "Mid", "omega", "zeta" }, store.Keys.Select(k => k.Name));
    }

    [Fact]
    public void Load_DropsRecordsWhoseSecretNoLongerParses_WithoutDeletingThem()
    {
        var storage = new FakeRecordStore();
        storage.Records["a"] = new StoredAuthKey("a", "good", "ed25519-sec:AAAA");
        storage.Records["b"] = new StoredAuthKey("b", "corrupt", "garbage");

        var store = StoreOver(storage);

        Assert.Equal("good", Assert.Single(store.Keys).Name);
        // Derivation goes through the native library; a transient failure there
        // must not destroy a stored key.
        Assert.Equal(2, storage.Records.Count);
    }

    [Fact]
    public void AnUnreadableStore_ReportsAndRefusesEveryWrite()
    {
        var storage = new FakeRecordStore { FailLoad = true };
        var store = StoreOver(storage);

        Assert.NotNull(store.LoadError);
        Assert.Empty(store.Keys);
        // An empty list here is a partial view of the real one, so adding,
        // renaming or deleting against it would only make things worse.
        Assert.Equal(store.LoadError, store.Add("laptop", "ed25519-sec:AAAA", out _));
        Assert.Equal(store.LoadError, store.Rename("a", "laptop"));
        Assert.Equal(store.LoadError, store.Delete("a"));
        Assert.Empty(storage.Records);
    }

    [Fact]
    public void AnEmptyStore_IsNotALoadFailure()
    {
        var store = StoreOver(new FakeRecordStore());

        Assert.Null(store.LoadError);
        Assert.Empty(store.Keys);
        Assert.Null(store.Find(""));
        Assert.Null(store.Find(null));
    }
}
