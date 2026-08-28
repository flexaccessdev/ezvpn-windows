using System.Text.Json;

namespace Ezvpn.Core.Interop;

/// <summary>
/// Client auth keypair primitives, via the Rust FFI. A secret key
/// (<c>ed25519-sec:…</c>) authenticates the tunnel handshake; its public key
/// (<c>ed25519-pub:…</c>) is not a secret — it is what the user puts on the
/// server's <c>authorized_keys</c> file, and it is re-derived from the secret
/// whenever needed rather than stored. The app's named key list lives in
/// <see cref="AuthKeyStore"/>.
///
/// Keys are never generated or parsed in .NET: the shared FlexAccess key format
/// is owned by the Rust side (<c>ezvpn_generate_client_key</c> /
/// <c>ezvpn_client_public_key</c>), exactly as in <c>ezvpn-apple</c> and
/// <c>ezvpn-android</c>.
/// </summary>
public static class AuthKey
{
    /// <summary>A generated keypair. Both halves are the encoded token forms.</summary>
    public sealed record Keypair(string SecretKey, string PublicKey);

    /// <summary>
    /// Generate a fresh ed25519 keypair, or null if the FFI misbehaved (or the
    /// system RNG was unavailable).
    /// </summary>
    public static Keypair? Generate()
    {
        // On a too-small buffer the FFI writes a truncated prefix of the key
        // document — secret material included — so zero it before it goes back on
        // the heap rather than leaving a partial secret behind.
        var buf = new byte[1024];
        try
        {
            if (EzvpnNative.GenerateClientKey(buf, (nuint)buf.Length) != 1)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(EzvpnNative.ReadCString(buf));
            var secret = doc.RootElement.GetProperty("secret_key").GetString();
            var publicKey = doc.RootElement.GetProperty("public_key").GetString();
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(publicKey))
            {
                return null;
            }
            return new Keypair(secret, publicKey);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            Array.Clear(buf);
        }
    }

    /// <summary>
    /// The public key of <paramref name="secret"/>, or null when it is not a
    /// valid secret key — which also makes this the validator for pasted keys.
    /// </summary>
    public static string? PublicKeyFor(string? secret)
    {
        var trimmed = (secret ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // Failure writes a diagnostic here instead of the key; the caller only
        // needs "not a valid secret", so it is not read back.
        var buf = new byte[256];
        return EzvpnNative.ClientPublicKey(trimmed, buf, (nuint)buf.Length) == 1
            ? EzvpnNative.ReadCString(buf)
            : null;
    }
}
