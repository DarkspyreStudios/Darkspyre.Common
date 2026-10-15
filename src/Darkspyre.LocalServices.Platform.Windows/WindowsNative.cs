using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Windows;

internal static partial class WindowsNative
{
    internal const uint Read = 0x80000000, Write = 0x40000000, ReadControl = 0x20000;
    internal const uint Directory = 0x10, ReparsePoint = 0x400;
    internal const int Missing = 2, SharingViolation = 32, LockViolation = 33;
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string path, uint access, uint sharing, in SecurityAttributes security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, uint length, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockFileEx(SafeFileHandle file, uint flags, uint reserved, uint low, uint high, ref Overlapped overlap);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateHandle(nint sourceProcess, SafeFileHandle source, nint targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();
    [LibraryImport("advapi32.dll")]
    internal static partial uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [LibraryImport("advapi32.dll")]
    internal static partial uint GetSecurityDescriptorLength(nint descriptor);
    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);
    internal static IOException Error(string operation, int? code = null)
    {
        int error = code ?? Marshal.GetLastPInvokeError();
        return new IOException($"Local service {operation} failed (Windows error {error}).", new Win32Exception(error));
    }
    internal static FileInformation Inspect(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out FileInformation info)) throw Error("inspect file");
        if ((info.Attributes & ReparsePoint) != 0 || ((info.Attributes & Directory) != 0) != directory || (!directory && info.Links != 1))
            throw new UnauthorizedAccessException("Service paths cannot be reparse points or hard links and must have the expected type.");
        return info;
    }
    internal static unsafe string FinalPath(SafeFileHandle handle)
    {
        uint length = GetFinalPathNameByHandle(handle, null, 0, 0);
        if (length == 0) throw Error("resolve file identity");
        char[] chars = new char[length + 1];
        fixed (char* pointer = chars)
        {
            uint actual = GetFinalPathNameByHandle(handle, pointer, (uint)chars.Length, 0);
            if (actual == 0 || actual >= chars.Length) throw Error("resolve file identity");
            string path = new(chars, 0, (int)actual);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes { internal uint Length; internal nint Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Overlapped { internal nuint Internal; internal nuint InternalHigh; internal uint Offset; internal uint OffsetHigh; internal nint Event; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInformation
    {
        internal uint Attributes;
        internal uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        internal readonly bool SameFile(FileInformation other) => this.Volume == other.Volume && this.IndexHigh == other.IndexHigh && this.IndexLow == other.IndexLow;
    }
}
