using Ezvpn.Core;

namespace Ezvpn.App.Services;

/// <summary>
/// The app's secrets, in the run's <see cref="ISecretBackend"/>: Windows
/// Credential Manager (<see cref="CredentialManager"/>), or memory under
/// <c>EZVPN_KEYCHAIN=mock</c> (see <see cref="KeychainMode"/> and
/// <see cref="UseMemory"/>). Nothing secret is ever written to the profile JSON.
///
/// Three kinds of entry live here, distinguished by target prefix:
/// <list type="bullet">
/// <item><c>ezvpn-profile-key:&lt;profileId&gt;</c> — the profile's copy of the
/// ed25519 auth key it connects with (what <c>ezvpn_start</c> is handed).</item>
/// <item><c>ezvpn-relay:&lt;profileId&gt;</c> — the optional shared relay bearer
/// token.</item>
/// <item><c>ezvpn-key:&lt;keyId&gt;</c> — one record of the app's named auth-key
/// list (see <see cref="CredentialAuthKeyStore"/>).</item>
/// </list>
/// </summary>
public static class SecretStore
{
    /// <summary>Where this run keeps its secrets.</summary>
    public static ISecretBackend Backend { get; private set; } = new CredentialManager();

    /// <summary>Whether this run keeps its secrets in memory (<see cref="UseMemory"/>).</summary>
    public static bool IsMemory => Backend is MemorySecretBackend;

    /// <summary>
    /// Keep every secret in memory for the rest of the process instead of in
    /// Credential Manager: the <c>EZVPN_KEYCHAIN=mock</c> backend. Call once at
    /// launch, before anything reads a secret.
    /// </summary>
    public static void UseMemory() => Backend = new MemorySecretBackend();

    // Deliberately not the "ezvpn:<profileId>" target that held the pre-shared
    // auth token this replaced: that credential stores a different kind of secret,
    // and reading one back as an ed25519 key would only produce a baffling
    // "must decode to 32 bytes" at connect time. A profile from an older build
    // therefore has no auth key at all, which is exactly what it is.
    private static string AuthKeyTargetFor(Guid id) => "ezvpn-profile-key:" + id.ToString("N");
    private static string RelayTargetFor(Guid id) => "ezvpn-relay:" + id.ToString("N");

    /// <summary>Store (or overwrite) a profile's copy of its auth key.</summary>
    public static void SaveAuthKey(Guid id, string secret) => Backend.Write(AuthKeyTargetFor(id), secret);

    /// <summary>Read a profile's auth key, or null if none is stored.</summary>
    public static string? LoadAuthKey(Guid id) => Backend.Read(AuthKeyTargetFor(id));

    /// <summary>Delete a profile's stored auth key (no-op if absent).</summary>
    public static void DeleteAuthKey(Guid id) => Backend.Delete(AuthKeyTargetFor(id));

    /// <summary>Store (or overwrite) the optional relay token for a profile.</summary>
    public static void SaveRelayToken(Guid id, string token) => Backend.Write(RelayTargetFor(id), token);

    /// <summary>Read the relay token for a profile, or null if none is stored.</summary>
    public static string? LoadRelayToken(Guid id) => Backend.Read(RelayTargetFor(id));

    /// <summary>Delete the stored relay token for a profile (no-op if absent).</summary>
    public static void DeleteRelayToken(Guid id) => Backend.Delete(RelayTargetFor(id));
}
