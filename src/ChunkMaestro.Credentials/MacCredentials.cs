using System.Runtime.InteropServices;

namespace ChunkMaestro.Credentials;

internal sealed class MacCredentials : INativeCredentials
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<nint> SecurityLibrary = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<nint> CoreLibrary = new(() => NativeLibrary.Load(Core));
    private static readonly object InteractionGate = new();
    private readonly nint keychain;

    internal MacCredentials(nint keychain = default)
    {
        this.keychain = keychain;
        if (keychain == 0)
        {
            Check(SecKeychainCopyDefault(out this.keychain));
        }
        else
        {
            CFRetain(keychain);
        }
    }

    internal static MacCredentials OpenFixture(string path)
    {
        Check(SecKeychainOpen(System.Text.Encoding.UTF8.GetBytes(path + "\0"), out nint handle));
        try
        {
            return new MacCredentials(handle);
        }
        finally
        {
            CFRelease(handle);
        }
    }

    [DllImport(Security)]
    private static extern int SecKeychainOpen(byte[] path, out nint keychain);

    internal static nint Symbol(string name, bool core = false)
    {
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(core ? CoreLibrary.Value : SecurityLibrary.Value, name));
    }

    [DllImport(Core)]
    internal static extern void CFRelease(nint value);
    [DllImport(Core)]
    private static extern nint CFRetain(nint value);
    [DllImport(Core)]
    private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
    [DllImport(Core)]
    private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(Core)]
    private static extern nint CFStringCreateWithCString(nint allocator, byte[] value, uint encoding);
    [DllImport(Core)]
    private static extern nint CFArrayCreate(nint allocator, nint[] values, nint count, nint callbacks);
    [DllImport(Core)]
    private static extern nint CFDataCreateWithBytesNoCopy(nint allocator, nint bytes, nint length, nint deallocator);
    [DllImport(Core)]
    private static extern nint CFDataGetLength(nint data);
    [DllImport(Core)]
    private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(Security)]
    private static extern int SecItemAdd(nint query, nint result);
    [DllImport(Security)]
    private static extern int SecItemCopyMatching(nint query, out nint result);
    [DllImport(Security)]
    private static extern int SecItemDelete(nint query);
    [DllImport(Security)]
    internal static extern int SecKeychainCreate(byte[] path, uint length, byte[] password, byte prompt, nint access, out nint result);
    [DllImport(Security)]
    internal static extern int SecKeychainDelete(nint keychain);
    [DllImport(Security)]
    private static extern int SecKeychainCopyDefault(out nint keychain);
    [DllImport(Security)]
    private static extern int SecKeychainGetStatus(nint keychain, out uint status);
    [DllImport(Security)]
    internal static extern int SecKeychainCopySearchList(out nint list);
    [DllImport(Core)]
    internal static extern byte CFEqual(nint first, nint second);
    [DllImport(Security)]
    internal static extern int SecKeychainLock(nint keychain);
    [DllImport(Security)]
    private static extern int SecKeychainGetUserInteractionAllowed(out byte allowed);
    [DllImport(Security)]
    private static extern int SecKeychainSetUserInteractionAllowed(byte allowed);

    internal static CredentialFailure Map(int status)
    {
        return status switch
        {
            -25300 => CredentialFailure.Absent,
            -25299 => CredentialFailure.Conflict,
            -25293 or -128 => CredentialFailure.Denied,
            -25308 or -25315 => CredentialFailure.Locked,
            _ => CredentialFailure.Unavailable
        };
    }

    private static void Check(int status)
    {
        if (status != 0)
        {
            throw new CredentialException(Map(status));
        }
    }

    private static nint Query()
    {
        // NULL callbacks: values are owned explicitly until each native call returns.
        nint query = CFDictionaryCreateMutable(0, 0, 0, 0);
        if (query == 0)
        {
            throw new CredentialException(CredentialFailure.Unavailable);
        }
        CFDictionarySetValue(query, Symbol("kSecClass"), Symbol("kSecClassGenericPassword"));
        CFDictionarySetValue(query, Symbol("kSecUseAuthenticationUI"), Symbol("kSecUseAuthenticationUIFail"));
        CFDictionarySetValue(query, Symbol("kSecUseNoAuthenticationUI"), Symbol("kCFBooleanTrue", true));
        return query;
    }

    // File Keychain ignores the modern authentication-UI option on this host.
    // This is process-local state, restored under a shared gate on every exit.
    private void WithQuery(string name, bool add, Action<nint> action)
    {
        lock (InteractionGate)
        {
            Check(SecKeychainGetUserInteractionAllowed(out byte allowed));
            Check(SecKeychainSetUserInteractionAllowed(0));
            try
            {
                WithQueryCore(name, add, action);
            }
            finally
            {
                Check(SecKeychainSetUserInteractionAllowed(allowed));
            }
        }
    }

    private void WithQueryCore(string name, bool add, Action<nint> action)
    {
        Check(SecKeychainGetStatus(keychain, out uint status));
        if ((status & 1) == 0)
        {
            throw new CredentialException(CredentialFailure.Locked);
        }
        nint query = Query();
        nint account = CFStringCreateWithCString(0, System.Text.Encoding.UTF8.GetBytes(name + "\0"), 0x08000100);
        nint service = CFStringCreateWithCString(0, "ChunkMaestro.credentials.v1\0"u8.ToArray(), 0x08000100);
        nint list = 0;
        try
        {
            CFDictionarySetValue(query, Symbol("kSecAttrService"), service);
            CFDictionarySetValue(query, Symbol("kSecAttrAccount"), account);
            if (keychain != 0)
            {
                if (add)
                {
                    CFDictionarySetValue(query, Symbol("kSecUseKeychain"), keychain);
                }
                else
                {
                    list = CFArrayCreate(0, [keychain], 1, 0);
                    CFDictionarySetValue(query, Symbol("kSecMatchSearchList"), list);
                }
            }
            action(query);
        }
        finally
        {
            if (list != 0)
            {
                CFRelease(list);
            }
            CFRelease(service);
            CFRelease(account);
            CFRelease(query);
        }
    }

    public unsafe void Create(string name, byte[] secret)
    {
        fixed (byte* pointer = secret)
        {
            nint data = CFDataCreateWithBytesNoCopy(0, (nint) pointer, secret.Length, Symbol("kCFAllocatorNull", true));
            try
            {
                WithQuery(name, true, query =>
                {
                    CFDictionarySetValue(query, Symbol("kSecValueData"), data);
                    Check(SecItemAdd(query, 0));
                });
            }
            finally
            {
                CFRelease(data);
            }
        }
    }

    public unsafe byte[] Read(string name)
    {
        byte[]? bytes = null;
        WithQuery(name, false, query =>
        {
            CFDictionarySetValue(query, Symbol("kSecReturnData"), Symbol("kCFBooleanTrue", true));
            CFDictionarySetValue(query, Symbol("kSecMatchLimit"), Symbol("kSecMatchLimitOne"));
            Check(SecItemCopyMatching(query, out nint data));
            try
            {
                nint length = CFDataGetLength(data);
                if (length is < 1 or > 2560)
                {
                    throw new CredentialException(CredentialFailure.Unavailable);
                }
                bytes = new ReadOnlySpan<byte>((void*) CFDataGetBytePtr(data), (int) length).ToArray();
            }
            finally
            {
                CFRelease(data);
            }
        });
        return bytes!;
    }

    public void Delete(string name)
    {
        WithQuery(name, false, query => Check(SecItemDelete(query)));
    }

    public void Dispose()
    {
        if (keychain != 0)
        {
            CFRelease(keychain);
        }
    }
}
