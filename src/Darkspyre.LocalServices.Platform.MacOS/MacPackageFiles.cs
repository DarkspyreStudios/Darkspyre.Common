using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.MacOS;

internal static class MacPackageFiles
{
    internal static SafeFileHandle Open(string path, bool directory)
    {
        int fd = MacNative.Open(path, MacNative.CloseOnExec | 0x20000000 | (directory ? MacNative.Directory : 4)); // NOFOLLOW_ANY; NONBLOCK rejects special-file stalls.
        if (fd < 0) throw MacNative.Error("open package entry", Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        try
        {
            if (MacNative.Fstat(handle, out MacNative.FileStatus status) != 0) throw MacNative.Error("inspect package entry", Marshal.GetLastPInvokeError());
            if ((status.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)) throw new InvalidDataException("A package entry has an unsupported file type.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void RemoveTree(string root)
    {
        // Never follow a link, including during cleanup of an incomplete or externally damaged package.
        FileAttributes attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A cache directory cannot be a symbolic link.");
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0) RemoveTree(path);
            else if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
            else File.Delete(path);
        }
        Directory.Delete(root);
    }
}
