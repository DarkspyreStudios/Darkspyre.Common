using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.MacOS;

/// <summary>Darwin 64-bit ABI declarations from the macOS SDK's stat.h, mount.h, fcntl.h and acl.h.</summary>
[SupportedOSPlatform("macos")]
internal static partial class MacNative
{
    internal const int ReadWrite = 0x2;
    internal const int NoFollow = 0x100;
    internal const int NoFollowAny = 0x20000000; // O_NOFOLLOW_ANY: refuse a symlink in any component.
    internal const int Directory = 0x100000;
    internal const int CloseOnExec = 0x1000000;
    internal const int ExclusiveNonblocking = 2 | 4;
    internal const int Missing = 2;
    internal const int Exists = 17;
    internal const int WouldBlock = 35;
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(LibSystem, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, int flags);
    [LibraryImport(LibSystem, EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenAt(SafeFileHandle directory, string name, int flags);
    [LibraryImport(LibSystem, EntryPoint = "mkostempsat_np", SetLastError = true)]
    internal static partial int CreateTemporaryAt(SafeFileHandle directory, [In, Out] byte[] template, int suffixLength, int flags);
    [LibraryImport(LibSystem, EntryPoint = "renameatx_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RenameAt(SafeFileHandle fromDirectory, string from, SafeFileHandle toDirectory, string to, uint flags);
    [LibraryImport(LibSystem, EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnlinkAt(SafeFileHandle directory, string name, int flags);
    [LibraryImport(LibSystem, EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MakeDirectoryAt(SafeFileHandle directory, string name, uint mode);
    [LibraryImport(LibSystem, EntryPoint = "fsync", SetLastError = true)]
    internal static partial int Fsync(SafeFileHandle descriptor);
    // fcntl is variadic; F_FULLFSYNC takes no third argument, so the fixed two-argument form matches the ABI.
    [LibraryImport(LibSystem, EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlWithoutArgument(SafeFileHandle descriptor, int command);

    /// <summary>
    /// Asks the drive to write its whole cache to permanent storage (F_FULLFSYNC). A file system without F_FULLFSYNC reports
    /// ENOTSUP; its fsync is then the strongest flush it offers, as the .NET runtime also treats it.
    /// </summary>
    internal static void FullFsync(SafeFileHandle descriptor)
    {
        const int FullFsyncCommand = 51, Interrupted = 4, NotSupported = 45;
        while (FcntlWithoutArgument(descriptor, FullFsyncCommand) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == NotSupported) return;
            if (error != Interrupted) throw Error("F_FULLFSYNC", error);
        }
    }
    [LibraryImport(LibSystem, EntryPoint = "flock", SetLastError = true)]
    internal static partial int Flock(SafeFileHandle descriptor, int operation);
    [LibraryImport(LibSystem, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int IoctlWithoutArgument(SafeFileHandle descriptor, ulong request);
    [LibraryImport(LibSystem, EntryPoint = "fstat", SetLastError = true)]
    private static partial int FstatNative(SafeFileHandle descriptor, out FileStatus status);
    [LibraryImport(LibSystem, EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int FstatIntel(SafeFileHandle descriptor, out FileStatus status);
    internal static int Fstat(SafeFileHandle descriptor, out FileStatus status) => RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? FstatIntel(descriptor, out status) : FstatNative(descriptor, out status);
    [LibraryImport(LibSystem, EntryPoint = "fstatfs", SetLastError = true)]
    private static partial int FstatfsNative(SafeFileHandle descriptor, out FileSystemStatus status);
    [LibraryImport(LibSystem, EntryPoint = "fstatfs$INODE64", SetLastError = true)]
    private static partial int FstatfsIntel(SafeFileHandle descriptor, out FileSystemStatus status);
    internal static int Fstatfs(SafeFileHandle descriptor, out FileSystemStatus status) => RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? FstatfsIntel(descriptor, out status) : FstatfsNative(descriptor, out status);
    [LibraryImport(LibSystem, EntryPoint = "geteuid")]
    internal static partial uint EffectiveUserId();
    [LibraryImport(LibSystem, EntryPoint = "acl_get_fd_np", SetLastError = true)]
    private static partial nint GetAcl(SafeFileHandle descriptor, int type);
    [LibraryImport(LibSystem, EntryPoint = "acl_free", SetLastError = true)]
    private static partial int FreeAcl(nint acl);
    [LibraryImport(LibSystem, EntryPoint = "getpeereid", SetLastError = true)]
    internal static partial int GetPeerIdentity(System.Net.Sockets.SafeSocketHandle socket, out uint userId, out uint groupId);
    // Sets the calling thread's own working folder to the descriptor's directory; -1 returns the thread to the process's
    // working folder. libsystem_pthread exports it through libSystem, and no public SDK header declares it.
    [LibraryImport(LibSystem, EntryPoint = "pthread_fchdir_np", SetLastError = true)]
    internal static partial int ThreadChangeDirectory(int descriptor);

    internal static void RequirePlatform()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64))
            throw new PlatformNotSupportedException("Local service ownership requires macOS arm64 or x64.");
    }

    internal static IOException Error(string operation, int error) =>
        new($"Local service {operation} failed (errno {error}).", new Win32Exception(error));

    internal static FileStatus InspectPrivate(SafeFileHandle descriptor, bool directory, bool readOnly = false, bool executable = false)
    {
        if (Fstat(descriptor, out FileStatus status) != 0) throw Error("fstat", Marshal.GetLastPInvokeError());
        int expectedType = directory ? 0x4000 : 0x8000;
        int expectedMode = readOnly ? (directory || executable ? 0x140 : 0x100) : directory ? 0x1c0 : 0x180; // 0500/0400 sealed; 0700/0600 writable.
        if ((status.Mode & 0xf000) != expectedType || (status.Mode & 0xfff) != expectedMode
            || status.UserId != EffectiveUserId() || (!directory && status.LinkCount != 1))
            throw new UnauthorizedAccessException("The service path must be user-owned, private and of the expected type; regular files must have one link.");
        nint acl = GetAcl(descriptor, 0x100); // ACL_TYPE_EXTENDED
        if (acl != 0)
        {
            if (FreeAcl(acl) != 0) throw Error("acl_free", Marshal.GetLastPInvokeError());
            throw new UnauthorizedAccessException("A service-private path must not have an extended ACL.");
        }
        int error = Marshal.GetLastPInvokeError();
        if (error != Missing) throw Error("acl_get_fd_np", error);
        return status;
    }

    // Darwin __DARWIN_STRUCT_STAT64: only the inspected fields are named; the kernel writes all 144 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct FileStatus
    {
        [FieldOffset(0)] internal int Device;
        [FieldOffset(4)] internal ushort Mode;
        [FieldOffset(6)] internal ushort LinkCount;
        [FieldOffset(8)] internal ulong Inode;
        [FieldOffset(16)] internal uint UserId;
    }

    // Darwin __DARWIN_STRUCT_STATFS64 has flags at offset 64 and a 16-byte filesystem name at offset 72.
    [StructLayout(LayoutKind.Explicit, Size = 2168)]
    internal unsafe struct FileSystemStatus
    {
        [FieldOffset(64)] internal uint Flags;
        [FieldOffset(72)] internal fixed byte Name[16];
    }
}
