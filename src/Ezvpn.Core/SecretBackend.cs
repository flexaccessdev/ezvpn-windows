namespace Ezvpn.Core;

/// <summary>
/// Where the app's secrets are kept: generic credentials addressed by target
/// name. Windows Credential Manager in a normal run (<c>CredentialManager</c> in
/// the app), <see cref="MemorySecretBackend"/> under <c>EZVPN_KEYCHAIN=mock</c>
/// and in the tests.
/// </summary>
public interface ISecretBackend
{
    /// <summary>Store <paramref name="value"/> under <paramref name="target"/>, overwriting any existing one.</summary>
    void Write(string target, string value);

    /// <summary>
    /// The value under <paramref name="target"/>, or null if none is stored. Any
    /// other failure throws, so a store that cannot be read is never mistaken
    /// for an empty one.
    /// </summary>
    string? Read(string target);

    /// <summary>Remove <paramref name="target"/> (no-op if absent).</summary>
    void Delete(string target);

    /// <summary>
    /// Every target that starts with <paramref name="prefix"/>, as (target
    /// suffix, value) pairs. An empty result means there are none; a failure to
    /// enumerate throws.
    /// </summary>
    IReadOnlyList<(string Suffix, string Value)> Enumerate(string prefix);
}

/// <summary>
/// In-memory stand-in for Credential Manager: the <c>EZVPN_KEYCHAIN=mock</c>
/// backend (see <see cref="KeychainMode"/>), and what the tests run against.
/// Targets compare case-insensitively, as Credential Manager's do. Thread-safe.
/// </summary>
public sealed class MemorySecretBackend : ISecretBackend
{
    private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);

    public void Write(string target, string value)
    {
        lock (_items)
        {
            _items[target] = value;
        }
    }

    public string? Read(string target)
    {
        lock (_items)
        {
            return _items.TryGetValue(target, out var value) ? value : null;
        }
    }

    public void Delete(string target)
    {
        lock (_items)
        {
            _items.Remove(target);
        }
    }

    public IReadOnlyList<(string Suffix, string Value)> Enumerate(string prefix)
    {
        lock (_items)
        {
            return _items
                .Where(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Key[prefix.Length..], e.Value))
                .ToList();
        }
    }
}
