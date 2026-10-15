using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices;

// Native file operations used by the single cache, registration and launch-budget implementation.
internal abstract class ServiceFileSystem
{
    internal abstract ServiceDirectory OpenPrivateDirectory(string path);
    internal abstract SafeFileHandle Open(string path, bool directory);
    internal abstract void InspectPrivate(SafeFileHandle handle, bool directory, bool readOnly = false, bool executable = false);
    internal abstract bool IsExecutable(string path);
    internal abstract FileStream CreateFile(string path);
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
    internal abstract void PublishDirectory(string temporaryName, string finalName);
    public abstract void Dispose();
}
