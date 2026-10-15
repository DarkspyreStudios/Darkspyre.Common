using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Linux;

// glibc stat layouts follow sysdeps/unix/sysv/linux/{x86/,}bits/struct_stat.h.
internal static partial class LinuxNative
{
    private static readonly bool Arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    // ARM64 overrides the generic Linux open flags in arch/arm64/include/uapi/asm/fcntl.h.
    internal static readonly int NoFollow = Arm64 ? 0x8000 : 0x20000;
    internal static readonly int Directory = Arm64 ? 0x4000 : 0x10000;
    internal const int ReadWrite = 2, CloseOnExec = 0x80000;
    internal const int Create = 0x40, Exclusive = 0x80, Missing = 2, Exists = 17, WouldBlock = 11;
    private const string LibC = "libc.so.6";
    [LibraryImport(LibC, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, int flags, uint mode = 0);
    [LibraryImport(LibC, EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenAt(SafeFileHandle directory, string name, int flags, uint mode = 0);
    [LibraryImport(LibC, EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MakeDirectoryAt(SafeFileHandle directory, string name, uint mode);
    [LibraryImport(LibC, EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RenameAt(SafeFileHandle fromDirectory, string from, SafeFileHandle toDirectory, string to, uint flags);
    [LibraryImport(LibC, EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UnlinkAt(SafeFileHandle directory, string name, int flags);
    [LibraryImport(LibC, EntryPoint = "flock", SetLastError = true)]
    internal static partial int Flock(SafeFileHandle descriptor, int operation);
    [LibraryImport(LibC, EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int SetDescriptorFlags(SafeFileHandle descriptor, int command, int flags);
    [LibraryImport(LibC, EntryPoint = "fstat", SetLastError = true)]
    private static partial int FstatX64(SafeFileHandle descriptor, out FileStatusX64 status);
    [LibraryImport(LibC, EntryPoint = "fstat", SetLastError = true)]
    private static partial int FstatArm64(SafeFileHandle descriptor, out FileStatusArm64 status);
    [LibraryImport(LibC, EntryPoint = "geteuid")]
    internal static partial uint EffectiveUserId();
    [LibraryImport(LibC, EntryPoint = "getsockopt", SetLastError = true)]
    internal static partial int GetPeerIdentity(SafeSocketHandle socket, int level, int option, out PeerCredentials credentials, ref uint length);
    internal static void RequirePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Local service ownership requires Linux x64 or ARM64 with glibc.");
    }
    internal static int Fstat(SafeFileHandle descriptor, out FileStatus status)
    {
        if (Arm64)
        {
            int result = FstatArm64(descriptor, out FileStatusArm64 native);
            status = result == 0 ? new(native.Device, native.Inode, native.LinkCount, native.Mode, native.UserId) : default;
            return result;
        }
        else
        {
            int result = FstatX64(descriptor, out FileStatusX64 native);
            status = result == 0 ? new(native.Device, native.Inode, native.LinkCount, native.Mode, native.UserId) : default;
            return result;
        }
    }
    internal static IOException Error(string operation, int error) => new($"Local service {operation} failed (errno {error}).", new Win32Exception(error));
    internal static FileStatus InspectPrivate(SafeFileHandle descriptor, bool directory, bool readOnly = false, bool executable = false)
    {
        if (Fstat(descriptor, out FileStatus status) != 0) throw Error("fstat", Marshal.GetLastPInvokeError());
        int expectedMode = readOnly ? (directory || executable ? 0x140 : 0x100) : directory ? 0x1c0 : 0x180;
        if ((status.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || (status.Mode & 0xfff) != expectedMode
            || status.UserId != EffectiveUserId() || (!directory && status.LinkCount != 1))
            throw new UnauthorizedAccessException("The service path must be user-owned, private and of the expected type; regular files must have one link.");
        return status;
    }
    internal readonly record struct FileStatus(ulong Device, ulong Inode, ulong LinkCount, uint Mode, uint UserId);

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct FileStatusX64
    {
        [FieldOffset(0)] internal ulong Device;
        [FieldOffset(8)] internal ulong Inode;
        [FieldOffset(16)] internal ulong LinkCount;
        [FieldOffset(24)] internal uint Mode;
        [FieldOffset(28)] internal uint UserId;
    }
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct FileStatusArm64
    {
        [FieldOffset(0)] internal ulong Device;
        [FieldOffset(8)] internal ulong Inode;
        [FieldOffset(16)] internal uint Mode;
        [FieldOffset(20)] internal uint LinkCount;
        [FieldOffset(24)] internal uint UserId;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PeerCredentials { internal int ProcessId; internal uint UserId; internal uint GroupId; }
}
