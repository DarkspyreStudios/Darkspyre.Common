using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.MacOS;

/// <summary>An OS-released, nonblocking shared or exclusive lease on a permanent file. Disposing never unlinks the file.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacFileLease : IServicePackageLease
{
    private readonly SafeFileHandle descriptor;

    private MacFileLease(SafeFileHandle descriptor)
    {
        this.descriptor = descriptor;
        MacNative.FileStatus status = MacNative.InspectPrivate(descriptor, directory: false);
        this.Device = status.Device;
        this.Inode = status.Inode;
    }

    /// <summary>The device identity of the leased file.</summary>
    public int Device { get; }
    /// <summary>The inode identity of the leased file.</summary>
    public ulong Inode { get; }
    /// <summary>Whether the lease descriptor remains open.</summary>
    public bool IsHeld => !this.descriptor.IsClosed;
    internal SafeFileHandle Descriptor => this.descriptor;

    internal static MacFileLease AdoptShared(SafeFileHandle descriptor, MacPrivateDirectory directory, string name)
    {
        using SafeFileHandle expected = directory.OpenPermanentFile(name);
        MacNative.FileStatus actual = MacNative.InspectPrivate(descriptor, directory: false);
        MacNative.FileStatus identity = MacNative.InspectPrivate(expected, directory: false);
        if (actual.Device != identity.Device || actual.Inode != identity.Inode)
            throw new InvalidDataException("The inherited package lease does not match its permanent cache identity.");
        if (MacNative.Flock(descriptor, 1 | 4) != 0) throw MacNative.Error("adopt package lease", Marshal.GetLastPInvokeError());
        if (MacNative.IoctlWithoutArgument(descriptor, 0x20006601) != 0) // FIOCLEX; ioctl takes no variadic argument for this operation.
            throw MacNative.Error("protect adopted lease from inheritance", Marshal.GetLastPInvokeError());
        return new MacFileLease(descriptor);
    }

    /// <summary>Returns null only when another open description holds the lease. Other errors are not contention.</summary>
    public static MacFileLease? TryAcquire(MacPrivateDirectory directory, string name) => TryAcquire(directory, name, shared: false);

    /// <summary>Shared leases pin immutable package payloads. Collection requires an exclusive lease on the same file.</summary>
    public static MacFileLease? TryAcquireShared(MacPrivateDirectory directory, string name) => TryAcquire(directory, name, shared: true);

    private static MacFileLease? TryAcquire(MacPrivateDirectory directory, string name, bool shared)
    {
        ArgumentNullException.ThrowIfNull(directory);
        SafeFileHandle descriptor = directory.OpenPermanentFile(name);
        try
        {
            if (MacNative.Flock(descriptor, shared ? 1 | 4 : MacNative.ExclusiveNonblocking) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == MacNative.WouldBlock) return null;
                throw MacNative.Error("flock", error);
            }
            var result = new MacFileLease(descriptor);
            descriptor = null!;
            return result;
        }
        finally { descriptor?.Dispose(); }
    }

    /// <summary>Closes the descriptor and releases its lease without unlinking the file.</summary>
    public void Dispose() => this.descriptor.Dispose();
}
