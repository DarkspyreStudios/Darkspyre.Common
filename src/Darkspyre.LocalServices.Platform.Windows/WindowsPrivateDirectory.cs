using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Windows;

internal sealed class WindowsPrivateDirectory : ServiceDirectory
{
    internal SafeFileHandle Handle { get; }
    internal WindowsNative.FileInformation Identity { get; }
    internal override string Path { get; }
    private WindowsPrivateDirectory(string path, SafeFileHandle handle)
    {
        this.Path = path; this.Handle = handle;
        WindowsFileSecurity.Inspect(handle, directory: true);
        this.Identity = WindowsNative.Inspect(handle, directory: true);
    }
    internal static WindowsPrivateDirectory OpenOrCreate(string path)
    {
        SafeFileHandle handle = OpenPath(path, directory: true, createDirectories: true);
        try { return new(System.IO.Path.GetFullPath(path), handle); }
        catch { handle.Dispose(); throw; }
    }
    // Null when the directory or a parent is missing; creates nothing.
    internal static WindowsPrivateDirectory? OpenExisting(string path)
    {
        SafeFileHandle handle;
        try { handle = OpenPath(path, directory: true); }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: WindowsNative.Missing or WindowsNative.PathMissing }) { return null; }
        try { return new(System.IO.Path.GetFullPath(path), handle); }
        catch { handle.Dispose(); throw; }
    }
    internal static SafeFileHandle OpenPath(string path, bool directory, bool createDirectories = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This local service driver requires Windows.");
        string full = ValidatePath(path);
        string root = System.IO.Path.GetPathRoot(full)!;
        List<SafeFileHandle> parents = [];
        SafeFileHandle? current = null;
        try
        {
            current = OpenNative(root, WindowsNative.ReadControl, 3, 3, directory: true);
            WindowsNative.Inspect(current, directory: true);
            string[] parts = full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string next = root;
            for (int index = 0; index < parts.Length; index++)
            {
                bool isDirectory = index < parts.Length - 1 || directory;
                next = System.IO.Path.Combine(next, parts[index]);
                parents.Add(current); current = null;
                if (createDirectories && isDirectory) new DirectoryInfo(next).Create((DirectorySecurity)WindowsFileSecurity.Create(directory: true));
                current = OpenNative(next, isDirectory ? WindowsNative.ReadControl : WindowsNative.Read, 3, 3, isDirectory);
                WindowsNative.Inspect(current, isDirectory);
            }
            if (!string.Equals(WindowsNative.FinalPath(current).TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The service path resolves to a different file identity.");
            SafeFileHandle result = current; current = null; return result;
        }
        finally { current?.Dispose(); foreach (SafeFileHandle parent in parents) parent.Dispose(); }
    }
    internal static string ValidatePath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('\0'))
            throw new ArgumentException("A service path must be an absolute local drive path.", nameof(path));
        string[] parts = path.Replace('/', '\\').Split('\\');
        if (parts.Skip(1).Any(p => p is "." or ".." || p.Contains(':') || p.EndsWith(' ') || p.EndsWith('.')))
            throw new ArgumentException("A service path cannot contain traversal or alternate stream components.", nameof(path));
        return System.IO.Path.GetFullPath(path);
    }
    internal static unsafe SafeFileHandle OpenNative(string path, uint access, uint sharing, uint disposition, bool directory)
    {
        byte[] descriptor = WindowsFileSecurity.Create(directory).GetSecurityDescriptorBinaryForm();
        fixed (byte* pointer = descriptor)
        {
            var security = new WindowsNative.SecurityAttributes { Length = (uint)Marshal.SizeOf<WindowsNative.SecurityAttributes>(), Descriptor = (nint)pointer };
            SafeFileHandle result = WindowsNative.CreateFile(path, access, sharing, in security, disposition,
                0x00200000u | (directory ? 0x02000000u : 0), 0); // OPEN_REPARSE_POINT and BACKUP_SEMANTICS
            if (!result.IsInvalid) return result;
            int error = Marshal.GetLastPInvokeError(); result.Dispose();
            throw WindowsNative.Error("open private path", error);
        }
    }
    internal string Child(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("A private entry must be a single path component.", nameof(name));
        return System.IO.Path.Combine(this.Path, name);
    }
    internal override byte[]? ReadPrivateFile(string name)
    {
        SafeFileHandle file;
        try { file = OpenNative(Child(name), WindowsNative.Read, 7, 3, directory: false); }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: WindowsNative.Missing }) { return null; }
        using (file)
        {
            WindowsFileSecurity.Inspect(file, directory: false);
            using var stream = new FileStream(file, FileAccess.Read);
            if (stream.Length is <= 0 or > 4096) throw new InvalidDataException("A private metadata file is outside its size limit.");
            byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            if (stream.Length != bytes.Length) throw new IOException("The private metadata changed during reading.");
            return bytes;
        }
    }
    internal override void PublishPrivateFile(string name, ReadOnlySpan<byte> bytes)
    {
        string destination = Child(name);
        if (bytes.Length is 0 or > 4096) throw new InvalidDataException("A private metadata file is outside its size limit.");
        string temporary = Child(".publish-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (SafeFileHandle file = OpenNative(temporary, WindowsNative.Write, 0, 1, directory: false))
            {
                WindowsFileSecurity.Inspect(file, directory: false);
                RandomAccess.Write(file, bytes, 0); RandomAccess.FlushToDisk(file);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
    internal void RemoveChild(string name) => File.Delete(Child(name));
    internal override IServicePackageLease? TryAcquire(string name, bool shared = false) => WindowsFileLease.TryAcquire(this, name, shared);
    internal override bool IsLeaseHeld(string name) => WindowsFileLease.IsHeldElsewhere(this, name);
    internal override void PublishDirectory(string temporaryName, string finalName) => System.IO.Directory.Move(Child(temporaryName), Child(finalName));
    public override void Dispose() => this.Handle.Dispose();
}
