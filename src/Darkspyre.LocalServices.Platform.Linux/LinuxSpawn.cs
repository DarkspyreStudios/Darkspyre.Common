using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Linux;

// Sizes and signatures follow glibc posix/spawn.h on Linux x64 and ARM64.
internal static partial class LinuxSpawn
{
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawn", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Spawn(out int processId, string path, nint actions, nint attributes, nint arguments, nint environment);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawnattr_init")]
    internal static partial int InitializeAttributes(nint attributes);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawnattr_destroy")]
    internal static partial int DestroyAttributes(nint attributes);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawnattr_setflags")]
    internal static partial int SetFlags(nint attributes, short flags);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawnattr_setsigmask")]
    internal static partial int SetSignalMask(nint attributes, nint signals);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawnattr_setsigdefault")]
    internal static partial int SetSignalDefaults(nint attributes, nint signals);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawn_file_actions_init")]
    internal static partial int InitializeActions(nint actions);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawn_file_actions_destroy")]
    internal static partial int DestroyActions(nint actions);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ChangeDirectory(nint actions, string path);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawn_file_actions_addopen", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenDescriptor(nint actions, int descriptor, string path, int flags, uint mode);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawn_file_actions_adddup2")]
    internal static partial int DuplicateDescriptor(nint actions, SafeFileHandle descriptor, int destination);
    [LibraryImport("libc.so.6", EntryPoint = "posix_spawn_file_actions_addclosefrom_np")]
    internal static partial int CloseFrom(nint actions, int from);
    [LibraryImport("libc.so.6", EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitPid(int processId, out int status, int options);
    internal static void Check(int error, string operation)
    {
        if (error != 0) throw LinuxNative.Error(operation, error);
    }
}
