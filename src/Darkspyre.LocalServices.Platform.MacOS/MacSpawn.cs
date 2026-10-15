using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darkspyre.LocalServices.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal static partial class MacSpawn
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    [LibraryImport(LibSystem, EntryPoint = "posix_spawnattr_init")]
    internal static partial int InitializeAttributes(out nint attributes);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawnattr_destroy")]
    internal static partial int DestroyAttributes(ref nint attributes);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawnattr_setflags")]
    internal static partial int SetFlags(ref nint attributes, short flags);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawnattr_setsigmask")]
    internal static partial int SetSignalMask(ref nint attributes, ref uint mask);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawnattr_setsigdefault")]
    internal static partial int SetSignalDefaults(ref nint attributes, ref uint mask);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawn_file_actions_init")]
    internal static partial int InitializeActions(out nint actions);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawn_file_actions_destroy")]
    internal static partial int DestroyActions(ref nint actions);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawn_file_actions_addopen", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenDescriptor(ref nint actions, int descriptor, string path, int flags, ushort mode);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawn_file_actions_adddup2")]
    internal static partial int DuplicateDescriptor(ref nint actions, Microsoft.Win32.SafeHandles.SafeFileHandle source, int destination);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawn_file_actions_addinherit_np")]
    internal static partial int InheritDescriptor(ref nint actions, int descriptor);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ChangeDirectory(ref nint actions, string path);
    [LibraryImport(LibSystem, EntryPoint = "posix_spawn", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Spawn(out int pid, string executable, ref nint actions, ref nint attributes, nint arguments, nint environment);
    [LibraryImport(LibSystem, EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitPid(int pid, out int status, int options);

    internal static void Check(int error, string operation)
    {
        // posix_spawn APIs return an errno directly, not through the thread's errno slot.
        if (error != 0) throw MacNative.Error(operation, error);
    }
}
