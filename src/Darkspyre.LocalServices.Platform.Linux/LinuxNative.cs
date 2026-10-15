using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Linux;

// Linux x64/glibc stat and spawn ABI. Layouts follow glibc sysdeps/unix/sysv/linux/x86/bits/struct_stat.h.
internal static partial class LinuxNative
{
    internal const int ReadWrite = 2, NoFollow = 0x20000, Directory = 0x10000, CloseOnExec = 0x80000;
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
    internal static partial int Fstat(SafeFileHandle descriptor, out FileStatus status);
    [LibraryImport(LibC, EntryPoint = "geteuid")]
    internal static partial uint EffectiveUserId();
    [LibraryImport(LibC, EntryPoint = "getsockopt", SetLastError = true)]
    internal static partial int GetPeerIdentity(SafeSocketHandle socket, int level, int option, out PeerCredentials credentials, ref uint length);
    internal static void RequirePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Local service ownership requires Linux x64.");
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
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct FileStatus
    {
        [FieldOffset(0)] internal ulong Device;
        [FieldOffset(8)] internal ulong Inode;
        [FieldOffset(16)] internal ulong LinkCount;
        [FieldOffset(24)] internal uint Mode;
        [FieldOffset(28)] internal uint UserId;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PeerCredentials { internal int ProcessId; internal uint UserId; internal uint GroupId; }
}
