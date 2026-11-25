using System.Reflection;
using System.Runtime.InteropServices;

namespace ChunkMaestro.PrivilegedHelper;

internal static class InspectorNativeLibrary
{
    internal const string Snapshot = "chunkmaestro_policy_snapshot_v1";
    internal const string SessionEscrow = "chunkmaestro_session_escrow_v1";

    static InspectorNativeLibrary()
    {
        NativeLibrary.SetDllImportResolver(typeof(InspectorNativeLibrary).Assembly, Resolve);
    }

    internal static void Initialize()
    {
        // Static initialization installs the sole assembly resolver exactly once.
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }
        if (name == "libc.so.6")
        {
            return NativeLibrary.Load(name, assembly, DllImportSearchPath.SafeDirectories);
        }
        string file = name switch
        {
            Snapshot => "libchunkmaestro_policy_snapshot_v1.so",
            SessionEscrow => "libchunkmaestro_session_escrow_v1.so",
            _ => throw new PlatformNotSupportedException()
        };
        // Fixed owned output only. Packaging/loader ancestry qualification is external.
        return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, file));
    }
}
