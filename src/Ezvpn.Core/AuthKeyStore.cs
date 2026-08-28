namespace Ezvpn.Core;

/// <summary>One persisted auth key. The public half is derived, never stored.</summary>
public sealed record StoredAuthKey(string Id, string Name, string Secret);

/// <summary>
/// Persistence for the app's named auth keys, one record per key. Implemented
/// against Windows Credential Manager (see <c>CredentialAuthKeyStore</c>); the
/// interface exists so <see cref="AuthKeyStore"/> stays pure and testable.
///
/// Unlike the Apple and Android apps — which keep the whole list as a single
/// Keychain / encrypted-preferences document — records are stored individually
/// here, because a Credential Manager blob caps out at 2560 bytes (about nine
/// keys). Per-record writes also mean an add, rename or delete never rewrites
/// the other keys.
/// </summary>
public interface IAuthKeyRecordStore
{
    /// <summary>
    /// Every stored record, in any order. Throws when the store exists but
    /// cannot be read — "nothing stored yet" is an empty list, never a failure.
    /// </summary>
    IReadOnlyList<StoredAuthKey> LoadAll();

    /// <summary>Add or overwrite the record with this id.</summary>
    void Save(StoredAuthKey record);

    /// <summary>Remove the record with this id (no-op if absent).</summary>
    void Delete(string id);
}

/// <summary>
/// The app's shared, named client auth keys — the same model the Apple and
/// Android apps use: one list of keypairs that profiles reference by id
/// (<see cref="TunnelProfile.AuthKeyId"/>), so several profiles can
/// authenticate with one device identity instead of pasting the same secret
/// into each.
///
/// Public halves are never persisted — each is re-derived from its secret on
/// load. The tunnel never reads this list: saving a profile copies the selected
/// key's secret into that profile's own credential, which is what
/// <c>ezvpn_start</c> is handed.
///
/// Mutating methods return a user-facing error message, or null on success, and
/// leave the in-memory list untouched when the write did not land — so what is
/// on screen is always what is actually stored.
/// </summary>
public sealed class AuthKeyStore
{
    /// <summary>One named keypair. <see cref="PublicKey"/> is derived, not persisted.</summary>
    public sealed record Key(string Id, string Name, string Secret, string PublicKey);

    private readonly IAuthKeyRecordStore _storage;
    private readonly Func<string, string?> _derivePublicKey;
    private readonly List<Key> _keys = new();

    /// <param name="storage">Where the records live.</param>
    /// <param name="derivePublicKey">
    /// Derives <c>ed25519-pub:…</c> from a secret, or returns null when the
    /// secret does not parse — normally <c>AuthKey.PublicKeyFor</c>. It is also
    /// the validator for a pasted secret, so the key format stays owned by the
    /// Rust core.
    /// </param>
    public AuthKeyStore(IAuthKeyRecordStore storage, Func<string, string?> derivePublicKey)
    {
        _storage = storage;
        _derivePublicKey = derivePublicKey;

        IReadOnlyList<StoredAuthKey> stored;
        try
        {
            stored = storage.LoadAll();
        }
        catch (Exception ex)
        {
            LoadError = $"Couldn't read the key list: {ex.Message} " +
                "Keys can't be changed until it can be read.";
            return;
        }

        foreach (var record in stored)
        {
            // A record whose secret no longer derives a public key is corrupt —
            // drop it from the view rather than carry an entry that can never
            // connect. It is not deleted from storage: the derivation goes
            // through the native library, and a transient failure there must not
            // destroy a stored key.
            var publicKey = _derivePublicKey(record.Secret);
            if (publicKey is not null)
            {
                _keys.Add(new Key(record.Id, record.Name, record.Secret, publicKey));
            }
        }

        _keys.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Why the stored keys couldn't be read, or null once they were (nothing
    /// stored counts as read: a fresh install genuinely has no keys). While this
    /// is set the list on screen is not what is stored, so every write is
    /// refused — adding a key whose name or secret duplicates an unseen one, or
    /// deleting from a list that isn't really empty, would only make it worse.
    /// </summary>
    public string? LoadError { get; }

    /// <summary>The keys, ordered by name.</summary>
    public IReadOnlyList<Key> Keys => _keys;

    /// <summary>The key with this id, or null if it isn't (or is no longer) listed.</summary>
    public Key? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : _keys.FirstOrDefault(k => k.Id == id);

    /// <summary>
    /// Validate and add a key: the name follows the profile-name rules (trimmed,
    /// required, unique ignoring case) and the secret must parse. The same
    /// keypair twice under two names is an accidental re-add, not a use case.
    /// </summary>
    public string? Add(string? name, string? secret, out Key? added)
    {
        added = null;
        if (LoadError is not null)
        {
            return LoadError;
        }

        var trimmedName = (name ?? "").Trim();
        var nameError = TunnelValidation.ValidateName(
            trimmedName, _keys.Select(k => k.Name), subject: "key");
        if (nameError is not null)
        {
            return nameError;
        }

        var trimmedSecret = (secret ?? "").Trim();
        var publicKey = _derivePublicKey(trimmedSecret);
        if (publicKey is null)
        {
            return "Not a valid secret key (expected ed25519-sec:…).";
        }

        var duplicate = _keys.FirstOrDefault(k => k.PublicKey == publicKey);
        if (duplicate is not null)
        {
            return $"Key \"{duplicate.Name}\" already holds this secret.";
        }

        var key = new Key(Guid.NewGuid().ToString("N"), trimmedName, trimmedSecret, publicKey);
        var error = Write(new StoredAuthKey(key.Id, key.Name, key.Secret), "save");
        if (error is not null)
        {
            return error;
        }

        Insert(key);
        added = key;
        return null;
    }

    /// <summary>Rename a key; returns a user-facing error message, or null.</summary>
    public string? Rename(string id, string? newName)
    {
        if (LoadError is not null)
        {
            return LoadError;
        }

        var index = _keys.FindIndex(k => k.Id == id);
        if (index < 0)
        {
            return "That key is no longer in the list.";
        }

        var trimmed = (newName ?? "").Trim();
        var nameError = TunnelValidation.ValidateName(
            trimmed, _keys.Where(k => k.Id != id).Select(k => k.Name), subject: "key");
        if (nameError is not null)
        {
            return nameError;
        }

        var previous = _keys[index];
        if (previous.Name == trimmed)
        {
            return null;
        }

        var renamed = previous with { Name = trimmed };
        var error = Write(new StoredAuthKey(renamed.Id, renamed.Name, renamed.Secret), "rename");
        if (error is not null)
        {
            return error;
        }

        _keys.RemoveAt(index);
        Insert(renamed);
        return null;
    }

    /// <summary>
    /// Delete a key; returns a user-facing error message when the removal
    /// couldn't be written back (the key stays listed then, since it is still
    /// stored). Profiles already saved with this key keep working: their own copy
    /// of the secret is what connects. Deleting here only removes the key from
    /// the list the profile editor picks from.
    /// </summary>
    public string? Delete(string id)
    {
        if (LoadError is not null)
        {
            return LoadError;
        }

        var index = _keys.FindIndex(k => k.Id == id);
        if (index < 0)
        {
            return null;
        }

        try
        {
            _storage.Delete(id);
        }
        catch (Exception ex)
        {
            return $"Couldn't delete the key: {ex.Message}";
        }

        _keys.RemoveAt(index);
        return null;
    }

    /// <summary>
    /// Write one record, turning any storage failure into a user-facing message.
    /// The caller updates the in-memory list only once this returns null, so a
    /// failed write leaves the list exactly as it was.
    /// </summary>
    private string? Write(StoredAuthKey record, string verb)
    {
        try
        {
            _storage.Save(record);
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't {verb} the key: {ex.Message}";
        }
    }

    /// <summary>Insert keeping <see cref="Keys"/> ordered by name.</summary>
    private void Insert(Key key)
    {
        var at = _keys.FindIndex(
            k => string.Compare(k.Name, key.Name, StringComparison.OrdinalIgnoreCase) > 0);
        _keys.Insert(at < 0 ? _keys.Count : at, key);
    }
}
