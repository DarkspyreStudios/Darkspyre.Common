using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Linux;

/// <summary>An OS-released, nonblocking shared or exclusive lease on a permanent file. Disposing never unlinks the file.</summary>
internal sealed class LinuxFileLease : IServicePackageLease
{
    private readonly SafeFileHandle descriptor;

    private LinuxFileLease(SafeFileHandle descriptor)
    {
        this.descriptor = descriptor;
        LinuxNative.FileStatus status = LinuxNative.InspectPrivate(descriptor, directory: false);
        this.Device = status.Device;
        this.Inode = status.Inode;
    }

    public ulong Device { get; }
    public ulong Inode { get; }
    public bool IsHeld => !this.descriptor.IsClosed;
    internal SafeFileHandle Descriptor => this.descriptor;

    internal static LinuxFileLease AdoptShared(SafeFileHandle descriptor, LinuxPrivateDirectory directory, string name)
    {
        using SafeFileHandle expected = directory.OpenPermanentFile(name);
        LinuxNative.FileStatus actual = LinuxNative.InspectPrivate(descriptor, directory: false);
        LinuxNative.FileStatus identity = LinuxNative.InspectPrivate(expected, directory: false);
        if (actual.Device != identity.Device || actual.Inode != identity.Inode)
            throw new InvalidDataException("The inherited package lease does not match its permanent cache identity.");
        if (LinuxNative.Flock(descriptor, 1 | 4) != 0) throw LinuxNative.Error("adopt package lease", Marshal.GetLastPInvokeError());
        if (LinuxNative.SetDescriptorFlags(descriptor, 2, 1) != 0) // F_SETFD, FD_CLOEXEC.
            throw LinuxNative.Error("protect adopted lease from inheritance", Marshal.GetLastPInvokeError());
        return new LinuxFileLease(descriptor);
    }

    /// <summary>Returns null only when another open description holds the lease. Other errors are not contention.</summary>
    public static LinuxFileLease? TryAcquire(LinuxPrivateDirectory directory, string name) => TryAcquire(directory, name, shared: false);

    /// <summary>Shared leases pin immutable package payloads. Collection requires an exclusive lease on the same file.</summary>
    public static LinuxFileLease? TryAcquireShared(LinuxPrivateDirectory directory, string name) => TryAcquire(directory, name, shared: true);

    internal static LinuxFileLease? TryAcquire(LinuxPrivateDirectory directory, string name, bool shared)
    {
        ArgumentNullException.ThrowIfNull(directory);
        SafeFileHandle descriptor = directory.OpenPermanentFile(name);
        try
        {
            if (LinuxNative.Flock(descriptor, shared ? 1 | 4 : 2 | 4) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == LinuxNative.WouldBlock) return null;
                throw LinuxNative.Error("flock", error);
            }
            var result = new LinuxFileLease(descriptor);
            descriptor = null!;
            return result;
        }
        finally { descriptor?.Dispose(); }
    }

    public void Dispose() => this.descriptor.Dispose();
}
