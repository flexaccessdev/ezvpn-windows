using System.Runtime.InteropServices;
using System.Text;
using Ezvpn.Core;

namespace Ezvpn.App.Services;

/// <summary>
/// Windows Credential Manager (<c>CRED_TYPE_GENERIC</c>, persisted per machine):
/// where a normal run keeps its secrets — the Windows analogue of the Apple
/// app's Keychain.
/// </summary>
public sealed class CredentialManager : ISecretBackend
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public void Write(string target, string value)
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

    public string? Read(string target)
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

    public void Delete(string target)
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

    public IReadOnlyList<(string Suffix, string Value)> Enumerate(string prefix)
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
