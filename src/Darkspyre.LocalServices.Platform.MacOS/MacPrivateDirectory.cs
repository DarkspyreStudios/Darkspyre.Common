using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.MacOS;

/// <summary>A private local directory reached without following any symlink. Its descriptor anchors child operations.</summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacPrivateDirectory : IDisposable
{
    private readonly SafeFileHandle handle;

    private MacPrivateDirectory(string path, SafeFileHandle handle)
    {
        this.Path = path;
        this.handle = handle;
        MacNative.FileStatus status = MacNative.InspectPrivate(handle, directory: true);
        this.Device = status.Device;
        this.Inode = status.Inode;
        CheckLocalFileSystem(handle);
    }

    /// <summary>The resolved private directory path.</summary>
    public string Path { get; }
    /// <summary>The device identity of the opened directory.</summary>
    public int Device { get; }
    /// <summary>The inode identity of the opened directory.</summary>
    public ulong Inode { get; }
    internal SafeFileHandle Handle => this.handle;

    /// <summary>Opens or creates the private directory and validates its ownership and permissions.</summary>
    public static MacPrivateDirectory OpenOrCreate(string path) => Open(path, create: true)!;

    // Null when the directory or a parent is missing; creates nothing.
    internal static MacPrivateDirectory? OpenExisting(string path) => Open(path, create: false);

    private static MacPrivateDirectory? Open(string path, bool create)
    {
        MacNative.RequirePlatform();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path) || path.Contains('\0'))
            throw new ArgumentException("A service directory requires an absolute path.", nameof(path));
        // Reject traversal before normalization; a symlink followed by '..' must not be hidden by GetFullPath.
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new ArgumentException("A service directory cannot contain traversal components.", nameof(path));
        // Open the permitted path directly. A walk from "/" opens ungranted ancestors such as /Volumes
        // even when the target is inside a granted folder. O_NOFOLLOW_ANY rejects a symlink in any component.
        int flags = MacNative.Directory | MacNative.NoFollowAny | MacNative.CloseOnExec;
        int fd = MacNative.Open(path, flags);
        if (fd >= 0) return Adopt(path, fd);
        int error = Marshal.GetLastPInvokeError();
        if (error != MacNative.Missing) throw MacNative.Error("open directory", error);
        if (!create) return null;
        return CreateFromPermittedAncestor(path, parts, flags);
    }

    // The missing suffix is created from the nearest existing ancestor that itself opens with O_NOFOLLOW_ANY.
    // An ancestor that cannot be opened is refused. Nothing is created by opening "/".
    private static MacPrivateDirectory CreateFromPermittedAncestor(string path, string[] parts, int flags)
    {
        SafeFileHandle? ancestor = null;
        int start = 0;
        try
        {
            for (int length = parts.Length - 1; length >= 1; length--)
            {
                int fd = MacNative.Open("/" + string.Join("/", parts, 0, length), flags);
                if (fd >= 0)
                {
                    ancestor = new SafeFileHandle((nint)fd, ownsHandle: true);
                    start = length;
                    break;
                }
                int error = Marshal.GetLastPInvokeError();
                if (error != MacNative.Missing) throw MacNative.Error("open directory", error);
            }
            if (ancestor is null) throw new IOException("A service directory requires an existing permitted ancestor.");
            SafeFileHandle current = ancestor;
            ancestor = null;
            try
            {
                for (int index = start; index < parts.Length; index++)
                {
                    string part = parts[index];
                    if (MacNative.MakeDirectoryAt(current, part, 0x1c0) != 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (error != MacNative.Exists) throw MacNative.Error("mkdirat", error);
                    }
                    int fd = MacNative.OpenAt(current, part, MacNative.Directory | MacNative.NoFollow | MacNative.CloseOnExec);
                    if (fd < 0) throw MacNative.Error("open directory", Marshal.GetLastPInvokeError());
                    var next = new SafeFileHandle((nint)fd, ownsHandle: true);
                    current.Dispose();
                    current = next;
                }
                var result = new MacPrivateDirectory(System.IO.Path.GetFullPath(path), current);
                current = null!; // Ownership transfers only after validation succeeds.
                return result;
            }
            finally { current?.Dispose(); }
        }
        finally { ancestor?.Dispose(); }
    }

    private static MacPrivateDirectory Adopt(string path, int fd)
    {
        SafeFileHandle opened = new((nint)fd, ownsHandle: true);
        try
        {
            var result = new MacPrivateDirectory(System.IO.Path.GetFullPath(path), opened);
            opened = null!; // Ownership transfers only after validation succeeds.
            return result;
        }
        finally { opened?.Dispose(); }
    }

    /// <summary>Opens or atomically creates a permanent file. Existing files are never truncated or replaced.</summary>
    internal SafeFileHandle OpenPermanentFile(string name)
    {
        RequireChildName(name);
        ObjectDisposedException.ThrowIf(this.handle.IsClosed, this);
        int fd = MacNative.OpenAt(this.handle, name, MacNative.ReadWrite | MacNative.NoFollow | MacNative.CloseOnExec);
        if (fd < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != MacNative.Missing) throw MacNative.Error("open permanent file", error);
            CreatePermanentFile(name);
            fd = MacNative.OpenAt(this.handle, name, MacNative.ReadWrite | MacNative.NoFollow | MacNative.CloseOnExec);
            if (fd < 0) throw MacNative.Error("open created file", Marshal.GetLastPInvokeError());
        }
        var result = new SafeFileHandle((nint)fd, ownsHandle: true);
        try { MacNative.InspectPrivate(result, directory: false); return result; }
        catch { result.Dispose(); throw; }
    }

    // Null when the permanent file does not exist; creates nothing.
    internal SafeFileHandle? OpenExistingPermanentFile(string name)
    {
        RequireChildName(name);
        ObjectDisposedException.ThrowIf(this.handle.IsClosed, this);
        int fd = MacNative.OpenAt(this.handle, name, MacNative.ReadWrite | MacNative.NoFollow | MacNative.CloseOnExec);
        if (fd < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == MacNative.Missing) return null;
            throw MacNative.Error("open permanent file", error);
        }
        var result = new SafeFileHandle((nint)fd, ownsHandle: true);
        try { MacNative.InspectPrivate(result, directory: false); return result; }
        catch { result.Dispose(); throw; }
    }

    private void CreatePermanentFile(string name)
    {
        // mkostempsat_np has a fixed ABI. Passing openat's variadic creation mode as a fixed argument is invalid on arm64.
        byte[] template = Encoding.UTF8.GetBytes(".create-XXXXXX\0");
        int fd = MacNative.CreateTemporaryAt(this.handle, template, 0, MacNative.CloseOnExec);
        if (fd < 0) throw MacNative.Error("create private file", Marshal.GetLastPInvokeError());
        using var temporary = new SafeFileHandle((nint)fd, ownsHandle: true);
        string temporaryName = Encoding.UTF8.GetString(template.AsSpan(0, Array.IndexOf(template, (byte)0)));
        bool published = false;
        Exception? failure = null;
        try
        {
            MacNative.InspectPrivate(temporary, directory: false);
            if (MacNative.RenameAt(this.handle, temporaryName, this.handle, name, 4) == 0) published = true; // RENAME_EXCL
            else
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != MacNative.Exists) throw MacNative.Error("publish permanent file", error);
            }
        }
        catch (Exception error) { failure = error; }
        if (!published && MacNative.UnlinkAt(this.handle, temporaryName, 0) != 0)
        {
            IOException cleanup = MacNative.Error("remove creation file", Marshal.GetLastPInvokeError());
            if (failure is not null) throw new AggregateException(failure, cleanup);
            throw cleanup;
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static void RequireChildName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw new ArgumentException("A private directory child must be a single path component.", nameof(name));
    }

    private static unsafe void CheckLocalFileSystem(SafeFileHandle handle)
    {
        if (MacNative.Fstatfs(handle, out MacNative.FileSystemStatus status) != 0)
            throw MacNative.Error("fstatfs", Marshal.GetLastPInvokeError());
        Span<byte> bytes = stackalloc byte[16];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = status.Name[i];
        int end = bytes.IndexOf((byte)0);
        string name = Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes);
        if ((status.Flags & 0x1000) == 0 || name is "virtiofs" or "virtio_fs" or "smbfs" or "nfs")
            throw new IOException("Service storage and rendezvous require a host-local filesystem, not a shared or remote mount.");
    }

    /// <summary>Closes the directory handle.</summary>
    public void Dispose() => this.handle.Dispose();
}
