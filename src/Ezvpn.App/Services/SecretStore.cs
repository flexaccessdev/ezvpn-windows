using System.Runtime.InteropServices;
using System.Text;

namespace Ezvpn.App.Services;

/// <summary>
/// The app's secrets, in Windows Credential Manager (<c>CRED_TYPE_GENERIC</c>) —
/// the Windows analogue of the Apple app's Keychain. Nothing secret is ever
/// written to the profile JSON.
///
/// Three kinds of entry live here, distinguished by target prefix:
/// <list type="bullet">
/// <item><c>ezvpn:&lt;profileId&gt;</c> — the profile's copy of the ed25519 auth
/// key it connects with (what <c>ezvpn_start</c> is handed).</item>
/// <item><c>ezvpn-relay:&lt;profileId&gt;</c> — the optional shared relay bearer
/// token.</item>
/// <item><c>ezvpn-key:&lt;keyId&gt;</c> — one record of the app's named auth-key
/// list (see <see cref="CredentialAuthKeyStore"/>).</item>
/// </list>
/// </summary>
public static class SecretStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    /// <summary>Target prefix of the named auth-key records.</summary>
    internal const string KeyRecordPrefix = "ezvpn-key:";

    private static string AuthKeyTargetFor(Guid id) => "ezvpn:" + id.ToString("N");
    private static string RelayTargetFor(Guid id) => "ezvpn-relay:" + id.ToString("N");

    /// <summary>Store (or overwrite) a profile's copy of its auth key.</summary>
    public static void SaveAuthKey(Guid id, string secret) => Write(AuthKeyTargetFor(id), secret);

    /// <summary>Read a profile's auth key, or null if none is stored.</summary>
    public static string? LoadAuthKey(Guid id) => Read(AuthKeyTargetFor(id));

    /// <summary>Delete a profile's stored auth key (no-op if absent).</summary>
    public static void DeleteAuthKey(Guid id) => Delete(AuthKeyTargetFor(id));

    /// <summary>Store (or overwrite) the optional relay token for a profile.</summary>
    public static void SaveRelayToken(Guid id, string token) => Write(RelayTargetFor(id), token);

    /// <summary>Read the relay token for a profile, or null if none is stored.</summary>
    public static string? LoadRelayToken(Guid id) => Read(RelayTargetFor(id));

    /// <summary>Delete the stored relay token for a profile (no-op if absent).</summary>
    public static void DeleteRelayToken(Guid id) => Delete(RelayTargetFor(id));

    /// <summary>Write a generic credential, overwriting any existing one.</summary>
    internal static void Write(string target, string value)
    {
        var blob = Encoding.Unicode.GetBytes(value);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWriteW(ref cred, 0))
            {
                throw new InvalidOperationException(
                    $"CredWrite failed (error {Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeCoTaskMem(targetPtr);
        }
    }

    /// <summary>
    /// Read a generic credential. Null means "not stored"; any other failure
    /// throws, so a store that cannot be read is never mistaken for an empty one.
    /// </summary>
    internal static string? Read(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
            {
                return null;
            }
            throw new InvalidOperationException($"CredRead failed (error {error})");
        }
        try
        {
            return BlobOf(Marshal.PtrToStructure<CREDENTIAL>(credPtr));
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <summary>Delete a generic credential (no-op if absent).</summary>
    internal static void Delete(string target)
    {
        if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
            {
                return;
            }
            throw new InvalidOperationException($"CredDelete failed (error {error})");
        }
    }

    /// <summary>
    /// Every generic credential whose target starts with <paramref name="prefix"/>,
    /// as (target suffix, value) pairs. An empty result means there are none; a
    /// failure to enumerate throws.
    /// </summary>
    internal static IReadOnlyList<(string Suffix, string Value)> Enumerate(string prefix)
    {
        // CredEnumerate's filter is a target-name prefix and must end in '*'.
        if (!CredEnumerateW(prefix + "*", 0, out var count, out var arrayPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
            {
                return Array.Empty<(string, string)>();
            }
            throw new InvalidOperationException($"CredEnumerate failed (error {error})");
        }
        try
        {
            var result = new List<(string, string)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                var target = Marshal.PtrToStringUni(cred.TargetName);
                // The filter is matched case-insensitively by the API, and a
                // generic credential of another type could in principle share the
                // prefix — keep only what we recognize.
                if (target is null ||
                    !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    cred.Type != CRED_TYPE_GENERIC)
                {
                    continue;
                }
                result.Add((target[prefix.Length..], BlobOf(cred)));
            }
            return result;
        }
        finally
        {
            CredFree(arrayPtr);
        }
    }

    /// <summary>Decode a credential's UTF-16 blob (empty when it carries none).</summary>
    private static string BlobOf(CREDENTIAL cred)
    {
        if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
        {
            return string.Empty;
        }
        return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2) ?? "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    // Classic DllImport (runtime marshalling) — the CREDENTIAL struct is not
    // supported by source-generated P/Invoke without disabling runtime marshalling.
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerateW(string filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
