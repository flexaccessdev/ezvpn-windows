using System.Text.Json;
using Ezvpn.Core;
using Xunit;

namespace Ezvpn.Core.Tests;

public class TunnelProfileTests
{
    private static TunnelProfile Sample() => new()
    {
        Name = "work",
        ServerNodeId = "abc123",
        AuthKeyId = "key-1",
        RelayUrls = { "https://relay.example/" },
        Routes = { "10.0.0.0/8" },
        Routes6 = { "fd00::/8" },
        MaxReconnectAttempts = 5,
    };

    [Fact]
    public void RoundTripsThroughJson()
    {
        var profile = Sample();

        var restored = JsonSerializer.Deserialize<TunnelProfile>(JsonSerializer.Serialize(profile));

        Assert.NotNull(restored);
        Assert.Equal(profile.Id, restored!.Id);
        Assert.Equal("work", restored.Name);
        Assert.Equal("abc123", restored.ServerNodeId);
        Assert.Equal("key-1", restored.AuthKeyId);
        Assert.Equal(profile.RelayUrls, restored.RelayUrls);
        Assert.Equal(profile.Routes, restored.Routes);
        Assert.Equal(profile.Routes6, restored.Routes6);
        Assert.Equal(5u, restored.MaxReconnectAttempts);
        Assert.Equal(profile.Instance, restored.Instance);
    }

    [Fact]
    public void SerializedShapeCarriesTheKeyIdAndNoSecret()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Sample()));

        Assert.Equal("key-1", doc.RootElement.GetProperty("authKeyId").GetString());
        // The key's secret lives in Credential Manager; only the id is on disk.
        Assert.DoesNotContain(
            "ed25519", JsonSerializer.Serialize(Sample()), StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileSurvivesDeletingTheKeyItNames_AndCanBeRepointed()
    {
        var storage = new FakeRecordStore();
        var keys = new AuthKeyStore(storage, FakeDerive);
        keys.Add("laptop", "ed25519-sec:AAAA", out var laptop);
        keys.Add("desktop", "ed25519-sec:BBBB", out var desktop);

        var profile = Sample();
        profile.AuthKeyId = laptop!.Id;
        Assert.Null(keys.Delete(laptop.Id));

        // Deleting a key removes only that key: the profile still loads, still
        // names the key it was saved with, and keeps every other setting.
        var restored = JsonSerializer.Deserialize<TunnelProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Equal(laptop.Id, restored.AuthKeyId);
        Assert.Equal("abc123", restored.ServerNodeId);
        Assert.Single(keys.Keys);

        // The editor shows no selection for it, and picking another key repairs
        // the profile — the id is just a reference, nothing cascades.
        Assert.Null(keys.Find(restored.AuthKeyId));
        restored.AuthKeyId = desktop!.Id;
        Assert.Equal("desktop", keys.Find(restored.AuthKeyId)?.Name);
    }

    /// <summary>Stand-in for the FFI derivation, as in <c>AuthKeyStoreTests</c>.</summary>
    private static string? FakeDerive(string secret) =>
        secret.StartsWith("ed25519-sec:", StringComparison.Ordinal)
            ? "ed25519-pub:" + secret["ed25519-sec:".Length..]
            : null;

    private sealed class FakeRecordStore : IAuthKeyRecordStore
    {
        private readonly Dictionary<string, StoredAuthKey> _records = new();

        public IReadOnlyList<StoredAuthKey> LoadAll() => _records.Values.ToList();

        public void Save(StoredAuthKey record) => _records[record.Id] = record;

        public void Delete(string id) => _records.Remove(id);
    }

    [Fact]
    public void AProfileWithoutAKeyDoesNotDeserialize()
    {
        // AuthKeyId is required with no default, so JSON that omits it fails
        // rather than loading as a keyless profile that could never connect.
        var json = """
            {"id":"6b1c9d0e-0000-0000-0000-000000000000","name":"work",
             "serverNodeId":"abc123","relayUrls":[],"routes":[],"routes6":[],
             "autoReconnect":true}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TunnelProfile>(json));
    }
}
