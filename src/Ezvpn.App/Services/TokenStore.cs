using System.Runtime.InteropServices;
using System.Text;

namespace Ezvpn.App.Services;

/// <summary>
/// Stores each profile's auth token in Windows Credential Manager
/// (<c>CRED_TYPE_GENERIC</c>), keyed by <c>ezvpn:&lt;profileId&gt;</c> — the
/// Windows analogue of the Apple app's Keychain. The profile JSON never contains
/// the token.
/// </summary>
public static class TokenStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    private static string TargetFor(Guid id) => "ezvpn:" + id.ToString("N");

    /// <summary>Store (or overwrite) the token for a profile.</summary>
    public static void Save(Guid id, string token)
    {
        var blob = Encoding.Unicode.GetBytes(token);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        var targetPtr = Marshal.StringToCoTaskMemUni(TargetFor(id));
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

    /// <summary>Read the token for a profile, or null if none is stored.</summary>
    public static string? Load(Guid id)
    {
        if (!CredReadW(TargetFor(id), CRED_TYPE_GENERIC, 0, out var credPtr))
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

    /// <summary>Delete the stored token for a profile (no-op if absent).</summary>
    public static void Delete(Guid id)
    {
        CredDeleteW(TargetFor(id), CRED_TYPE_GENERIC, 0);
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
