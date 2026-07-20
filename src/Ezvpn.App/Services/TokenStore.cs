using System.Runtime.InteropServices;
using System.Text;

namespace Ezvpn.App.Services;

/// <summary>
/// Stores each profile's secret tokens in Windows Credential Manager
/// (<c>CRED_TYPE_GENERIC</c>), keyed by <c>ezvpn:&lt;profileId&gt;</c> (the
/// required auth token) and <c>ezvpn-relay:&lt;profileId&gt;</c> (the optional
/// shared relay token) — the Windows analogue of the Apple app's Keychain. The
/// profile JSON never contains a token.
/// </summary>
public static class TokenStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    private static string TargetFor(Guid id) => "ezvpn:" + id.ToString("N");
    private static string RelayTargetFor(Guid id) => "ezvpn-relay:" + id.ToString("N");

    /// <summary>Store (or overwrite) the auth token for a profile.</summary>
    public static void Save(Guid id, string token) => Write(TargetFor(id), token);

    /// <summary>Read the auth token for a profile, or null if none is stored.</summary>
    public static string? Load(Guid id) => Read(TargetFor(id));

    /// <summary>Delete the stored auth token for a profile (no-op if absent).</summary>
    public static void Delete(Guid id) => CredDeleteW(TargetFor(id), CRED_TYPE_GENERIC, 0);

    /// <summary>Store (or overwrite) the optional relay token for a profile.</summary>
    public static void SaveRelay(Guid id, string token) => Write(RelayTargetFor(id), token);

    /// <summary>Read the relay token for a profile, or null if none is stored.</summary>
    public static string? LoadRelay(Guid id) => Read(RelayTargetFor(id));

    /// <summary>Delete the stored relay token for a profile (no-op if absent).</summary>
    public static void DeleteRelay(Guid id) => CredDeleteW(RelayTargetFor(id), CRED_TYPE_GENERIC, 0);

    private static void Write(string target, string token)
    {
        var blob = Encoding.Unicode.GetBytes(token);
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

    private static string? Read(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            return null;
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(credPtr);
        }
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

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
