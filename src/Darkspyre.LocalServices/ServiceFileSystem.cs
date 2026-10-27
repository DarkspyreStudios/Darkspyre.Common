using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices;

// Native file operations used by the single cache, registration and launch-budget implementation.
internal abstract class ServiceFileSystem
{
    internal abstract ServiceDirectory OpenPrivateDirectory(string path);
    // Null when the directory or any of its parents is missing; otherwise the same checks as OpenPrivateDirectory. Creates nothing.
    internal abstract ServiceDirectory? OpenExistingPrivateDirectory(string path);
    internal abstract SafeFileHandle Open(string path, bool directory);
    internal abstract void InspectPrivate(SafeFileHandle handle, bool directory, bool readOnly = false, bool executable = false);
    internal abstract bool IsExecutable(string path);
    internal abstract FileStream CreateFile(string path);

    /// <summary>
    /// Writes <paramref name="file"/>'s buffered bytes and hands them to the storage device. The bytes are durable only after a
    /// later <see cref="FlushDevice"/>, which a writer calls once after its last file.
    /// </summary>
    internal abstract void SyncFile(FileStream file);

    /// <summary>
    /// Makes every file already passed to <see cref="SyncFile"/> on the same device durable. A writer calls it once, after its
    /// last <see cref="SyncFile"/> and before it publishes the files.
    /// </summary>
    /// <param name="file">An open file on the device to flush, which has already been passed to <see cref="SyncFile"/>.</param>
    internal abstract void FlushDevice(FileStream file);
    internal abstract void CreateDirectory(string path);
    internal abstract void Seal(string path, bool executable);
    internal abstract void RemoveTree(string path);
    internal abstract IServicePackageLease AdoptLaunchLease(ServiceDirectory leases, string digest, string cachePath);
}

internal abstract class ServiceDirectory : IDisposable
{
    internal abstract string Path { get; }
    internal abstract byte[]? ReadPrivateFile(string name);
    internal abstract void PublishPrivateFile(string name, ReadOnlySpan<byte> bytes);
    internal abstract IServicePackageLease? TryAcquire(string name, bool shared = false);
    // Whether another open description holds the exclusive lease on the existing file; false when it is missing. Creates nothing.
    internal abstract bool IsLeaseHeld(string name);
    internal abstract void PublishDirectory(string temporaryName, string finalName);
    public abstract void Dispose();
}
