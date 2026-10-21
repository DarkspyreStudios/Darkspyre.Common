using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.MacOS;

internal sealed class MacServiceFileSystem : ServiceFileSystem
{
    internal const int InheritedPinDescriptor = 3;
    internal const string InheritedPinVariable = "DARKSPYRE_SERVICE_PACKAGE_FD";
    internal const string InheritedDigestVariable = "DARKSPYRE_SERVICE_PACKAGE_DIGEST";
    internal const string InheritedCacheVariable = "DARKSPYRE_SERVICE_PACKAGE_CACHE";


    internal override ServiceDirectory OpenPrivateDirectory(string path) => new DirectoryHandle(MacPrivateDirectory.OpenOrCreate(path));
    internal override SafeFileHandle Open(string path, bool directory) => MacPackageFiles.Open(path, directory);
    internal override void InspectPrivate(SafeFileHandle handle, bool directory, bool readOnly = false, bool executable = false)
        => MacNative.InspectPrivate(handle, directory, readOnly, executable);
    internal override bool IsExecutable(string path) => (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    internal override FileStream CreateFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.Asynchronous,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
    });
    // fsync hands the data to the drive; F_FULLFSYNC then flushes the drive's write cache once for all synced files.
    internal override void SyncFile(FileStream file)
    {
        file.Flush();
        if (MacNative.Fsync(file.SafeFileHandle) != 0) throw MacNative.Error("fsync", Marshal.GetLastPInvokeError());
    }
    internal override void FlushDevice(FileStream file) => MacNative.FullFsync(file.SafeFileHandle);
    internal override void CreateDirectory(string path) => Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    internal override void Seal(string path, bool executable) => File.SetUnixFileMode(path, executable ? UnixFileMode.UserRead | UnixFileMode.UserExecute : UnixFileMode.UserRead);
    internal override void RemoveTree(string path) => MacPackageFiles.RemoveTree(path);
    internal override IServicePackageLease AdoptLaunchLease(ServiceDirectory leases, string digest, string cachePath)
    {
        if (Environment.GetEnvironmentVariable(InheritedPinVariable) != "3"
            || Environment.GetEnvironmentVariable(InheritedDigestVariable) != digest
            || Environment.GetEnvironmentVariable(InheritedCacheVariable) != cachePath)
            throw new InvalidDataException("The service launch did not supply the expected inherited package lease.");
        Environment.SetEnvironmentVariable(InheritedPinVariable, null);
        Environment.SetEnvironmentVariable(InheritedDigestVariable, null);
        Environment.SetEnvironmentVariable(InheritedCacheVariable, null);
        var inherited = new SafeFileHandle((nint)InheritedPinDescriptor, ownsHandle: true);
        try { return MacFileLease.AdoptShared(inherited, ((DirectoryHandle)leases).Directory, digest + ".lock"); }
        catch { inherited.Dispose(); throw; }
    }
    private sealed class DirectoryHandle(MacPrivateDirectory directory) : ServiceDirectory
    {
        internal MacPrivateDirectory Directory => directory;
        internal override string Path => directory.Path;
        internal override byte[]? ReadPrivateFile(string name) => directory.ReadPrivateFile(name);
        internal override void PublishPrivateFile(string name, ReadOnlySpan<byte> bytes) => directory.PublishPrivateFile(name, bytes);
        internal override IServicePackageLease? TryAcquire(string name, bool shared = false)
            => shared ? MacFileLease.TryAcquireShared(directory, name) : MacFileLease.TryAcquire(directory, name);
        internal override void PublishDirectory(string temporaryName, string finalName)
        {
            if (MacNative.RenameAt(directory.Handle, temporaryName, directory.Handle, finalName, 4) != 0)
                throw MacNative.Error("publish immutable service build", Marshal.GetLastPInvokeError());
        }
        public override void Dispose() => directory.Dispose();
    }
}
