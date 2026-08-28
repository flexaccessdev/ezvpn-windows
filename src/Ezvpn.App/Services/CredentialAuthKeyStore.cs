using System.Text.Json;
using System.Text.Json.Serialization;
using Ezvpn.Core;

namespace Ezvpn.App.Services;

/// <summary>
/// The app's named auth keys in Windows Credential Manager: one credential per
/// key, targeted <c>ezvpn-key:&lt;keyId&gt;</c>, whose blob is
/// <c>{"name":…,"secret":…}</c>. The name is not sensitive but rides along with
/// the secret so a key is one atomic record.
///
/// One credential per key (rather than the single list document the Apple and
/// Android apps store) because a Credential Manager blob is capped at 2560
/// bytes — about nine keys — and because an add, rename or delete then never
/// rewrites the other keys.
/// </summary>
public sealed class CredentialAuthKeyStore : IAuthKeyRecordStore
{
    // Nullable on the way in: System.Text.Json does not enforce non-nullable
    // annotations, so an explicit `"secret": null` in a hand-edited credential
    // would land as null however this is declared. Better to say so and check.
    private sealed class Record
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("secret")]
        public string? Secret { get; set; }
    }

    public IReadOnlyList<StoredAuthKey> LoadAll()
    {
        var result = new List<StoredAuthKey>();
        foreach (var (id, json) in SecretStore.Enumerate(SecretStore.KeyRecordPrefix))
        {
            Record? record;
            try
            {
                record = JsonSerializer.Deserialize<Record>(json);
            }
            catch (JsonException)
            {
                // One unreadable record is not a broken store: skip it (the key
                // store drops undecodable entries from the list the same way)
                // rather than refusing every key.
                continue;
            }
            // A record with a missing or empty secret can never connect, so treat
            // it as corrupt and skip it like undecodable JSON — reading its length
            // unchecked would throw instead, taking the whole store down with it.
            // A missing name is not fatal: the key still works and can be renamed.
            if (record is not null && !string.IsNullOrEmpty(record.Secret))
            {
                result.Add(new StoredAuthKey(id, record.Name ?? "", record.Secret));
            }
        }
        return result;
    }

    public void Save(StoredAuthKey record) =>
        SecretStore.Write(
            SecretStore.KeyRecordPrefix + record.Id,
            JsonSerializer.Serialize(new Record { Name = record.Name, Secret = record.Secret }));

    public void Delete(string id) => SecretStore.Delete(SecretStore.KeyRecordPrefix + id);
}
