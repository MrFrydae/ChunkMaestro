using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ChunkMaestro.Credentials;

internal sealed class WindowsCredentials : INativeCredentials
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Write(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadNative(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteNative(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);

    internal static CredentialFailure Map(int status)
    {
        return status switch
        {
            1168 => CredentialFailure.Absent,
            5 => CredentialFailure.Denied,
            1312 => CredentialFailure.Unavailable,
            _ => CredentialFailure.Unavailable
        };
    }

    public unsafe void Create(string name, byte[] secret)
    {
        // Serialize cooperating processes; Windows has no create-only CredWrite flag.
        string digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));
        NamedWaitHandleOptions options = new()
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false
        };
        using Mutex mutex = new(false, "ChunkMaestro.credentials." + digest, options);
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Ownership was acquired. Always recheck the item before any write.
        }
        try
        {
            if (ReadNative(name, 1, 0, out nint existing))
            {
                ClearAndFree(existing);
                throw new CredentialException(CredentialFailure.Conflict);
            }
            int status = Marshal.GetLastPInvokeError();
            if (status != 1168)
            {
                throw new CredentialException(Map(status));
            }
            fixed (byte* pointer = secret)
            {
                Credential credential = new()
                {
                    Type = 1,
                    TargetName = name,
                    BlobSize = (uint) secret.Length,
                    Blob = (nint) pointer,
                    Persist = 2,
                    UserName = "ChunkMaestro"
                };
                if (!Write(ref credential, 0))
                {
                    throw new CredentialException(Map(Marshal.GetLastPInvokeError()));
                }
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public unsafe byte[] Read(string name)
    {
        if (!ReadNative(name, 1, 0, out nint pointer))
        {
            throw new CredentialException(Map(Marshal.GetLastPInvokeError()));
        }
        Credential credential = Marshal.PtrToStructure<Credential>(pointer);
        try
        {
            if (credential.BlobSize is < 1 or > 2560)
            {
                throw new CredentialException(CredentialFailure.Unavailable);
            }
            return new ReadOnlySpan<byte>((void*) credential.Blob, (int) credential.BlobSize).ToArray();
        }
        finally
        {
            ClearAndFree(pointer);
        }
    }

    private static unsafe void ClearAndFree(nint pointer)
    {
        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.Blob != 0 && credential.BlobSize <= 2560)
            {
                CryptographicOperations.ZeroMemory(new Span<byte>((void*) credential.Blob, (int) credential.BlobSize));
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Delete(string name)
    {
        if (!DeleteNative(name, 1, 0))
        {
            throw new CredentialException(Map(Marshal.GetLastPInvokeError()));
        }
    }

    public void Dispose()
    {
    }
}
