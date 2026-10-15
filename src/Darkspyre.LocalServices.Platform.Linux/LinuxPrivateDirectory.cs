using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Linux;

internal sealed class LinuxPrivateDirectory : ServiceDirectory
{
    internal SafeFileHandle Handle { get; }
    internal ulong Device { get; }
    internal ulong Inode { get; }
    private LinuxPrivateDirectory(string path, SafeFileHandle handle)
    {
        this.Path = path;
        this.Handle = handle;
        LinuxNative.FileStatus status = LinuxNative.InspectPrivate(handle, directory: true);
        this.Device = status.Device;
        this.Inode = status.Inode;
    }
    internal override string Path { get; }
    internal static LinuxPrivateDirectory OpenOrCreate(string path)
    {
        LinuxNative.RequirePlatform();
        SafeFileHandle handle = OpenPath(path, directory: true, createDirectories: true);
        try { return new(System.IO.Path.GetFullPath(path), handle); }
        catch { handle.Dispose(); throw; }
    }
    internal static SafeFileHandle OpenPath(string path, bool directory, bool createDirectories = false)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.Contains('\0')) throw new ArgumentException("A service path must be absolute.", nameof(path));
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) throw new ArgumentException("A service path cannot contain traversal.", nameof(path));
        int fd = LinuxNative.Open("/", LinuxNative.Directory | LinuxNative.CloseOnExec);
        if (fd < 0) throw LinuxNative.Error("open root", Marshal.GetLastPInvokeError());
        SafeFileHandle current = new((nint)fd, ownsHandle: true);
        try
        {
            for (int index = 0; index < parts.Length; index++)
            {
                bool isDirectory = index < parts.Length - 1 || directory;
                if (createDirectories && LinuxNative.MakeDirectoryAt(current, parts[index], 0x1c0) != 0 && Marshal.GetLastPInvokeError() != LinuxNative.Exists)
                    throw LinuxNative.Error("mkdirat", Marshal.GetLastPInvokeError());
                fd = LinuxNative.OpenAt(current, parts[index], LinuxNative.NoFollow | LinuxNative.CloseOnExec | (isDirectory ? LinuxNative.Directory : 0x800));
                if (fd < 0) throw LinuxNative.Error("open service path", Marshal.GetLastPInvokeError());
                current.Dispose();
                current = new((nint)fd, ownsHandle: true);
            }
            if (LinuxNative.Fstat(current, out LinuxNative.FileStatus status) != 0) throw LinuxNative.Error("inspect service path", Marshal.GetLastPInvokeError());
            if ((status.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)) throw new InvalidDataException("A service path has an unsupported file type.");
            SafeFileHandle result = current;
            current = null!;
            return result;
        }
        finally { current?.Dispose(); }
    }
    internal SafeFileHandle OpenPermanentFile(string name)
    {
        RequireName(name);
        int fd = LinuxNative.OpenAt(this.Handle, name, LinuxNative.ReadWrite | LinuxNative.NoFollow | LinuxNative.CloseOnExec | LinuxNative.Create | LinuxNative.Exclusive, 0x180);
        if (fd < 0 && Marshal.GetLastPInvokeError() == LinuxNative.Exists)
            fd = LinuxNative.OpenAt(this.Handle, name, LinuxNative.ReadWrite | LinuxNative.NoFollow | LinuxNative.CloseOnExec);
        if (fd < 0) throw LinuxNative.Error("open permanent file", Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        try { LinuxNative.InspectPrivate(handle, directory: false); return handle; }
        catch { handle.Dispose(); throw; }
    }
    internal override byte[]? ReadPrivateFile(string name)
    {
        RequireName(name);
        int fd = LinuxNative.OpenAt(this.Handle, name, LinuxNative.NoFollow | LinuxNative.CloseOnExec | 0x800);
        if (fd < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == LinuxNative.Missing) return null;
            throw LinuxNative.Error("read private file", error);
        }
        using var file = new SafeFileHandle((nint)fd, ownsHandle: true);
        LinuxNative.InspectPrivate(file, directory: false);
        using var stream = new FileStream(file, FileAccess.Read);
        if (stream.Length is <= 0 or > 4096) throw new InvalidDataException("A private metadata file is outside its size limit.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.Length != bytes.Length) throw new IOException("The private metadata changed during reading.");
        return bytes;
    }
    internal override void PublishPrivateFile(string name, ReadOnlySpan<byte> bytes)
    {
        RequireName(name);
        if (bytes.Length is 0 or > 4096) throw new InvalidDataException("A private metadata file is outside its size limit.");
        string temporary = ".publish-" + Guid.NewGuid().ToString("N");
        int fd = LinuxNative.OpenAt(this.Handle, temporary, LinuxNative.ReadWrite | LinuxNative.Create | LinuxNative.Exclusive | LinuxNative.NoFollow | LinuxNative.CloseOnExec, 0x180);
        if (fd < 0) throw LinuxNative.Error("create private metadata", Marshal.GetLastPInvokeError());
        using var file = new SafeFileHandle((nint)fd, ownsHandle: true);
        try
        {
            LinuxNative.InspectPrivate(file, directory: false);
            RandomAccess.Write(file, bytes, 0);
            RandomAccess.FlushToDisk(file);
            if (LinuxNative.RenameAt(this.Handle, temporary, this.Handle, name, 0) != 0)
                throw LinuxNative.Error("publish private metadata", Marshal.GetLastPInvokeError());
        }
        finally { RemoveChild(temporary); }
    }
    internal void RemoveChild(string name)
    {
        RequireName(name);
        if (LinuxNative.UnlinkAt(this.Handle, name, 0) != 0 && Marshal.GetLastPInvokeError() != LinuxNative.Missing)
            throw LinuxNative.Error("remove private entry", Marshal.GetLastPInvokeError());
    }
    internal override IServicePackageLease? TryAcquire(string name, bool shared = false) => LinuxFileLease.TryAcquire(this, name, shared);
    internal override void PublishDirectory(string temporaryName, string finalName)
    {
        RequireName(temporaryName); RequireName(finalName);
        if (LinuxNative.RenameAt(this.Handle, temporaryName, this.Handle, finalName, 1) != 0)
            throw LinuxNative.Error("publish immutable service build", Marshal.GetLastPInvokeError());
    }
    private static void RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw new ArgumentException("A private entry must be a single path component.", nameof(name));
    }
    public override void Dispose() => this.Handle.Dispose();
}
