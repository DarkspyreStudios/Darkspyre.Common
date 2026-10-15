using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Linux;

internal sealed class LinuxServiceFileSystem : ServiceFileSystem
{
    internal const int InheritedPinDescriptor = 3;
    internal const string InheritedPinVariable = "DARKSPYRE_SERVICE_PACKAGE_FD";
    internal const string InheritedDigestVariable = "DARKSPYRE_SERVICE_PACKAGE_DIGEST";
    internal const string InheritedCacheVariable = "DARKSPYRE_SERVICE_PACKAGE_CACHE";


    internal override ServiceDirectory OpenPrivateDirectory(string path) => LinuxPrivateDirectory.OpenOrCreate(path);
    internal override SafeFileHandle Open(string path, bool directory) => LinuxPrivateDirectory.OpenPath(path, directory);
    internal override void InspectPrivate(SafeFileHandle handle, bool directory, bool readOnly = false, bool executable = false)
        => LinuxNative.InspectPrivate(handle, directory, readOnly, executable);
    internal override bool IsExecutable(string path) => (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    internal override FileStream CreateFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
        Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
    });
    internal override void CreateDirectory(string path) => Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    internal override void Seal(string path, bool executable) => File.SetUnixFileMode(path, executable ? UnixFileMode.UserRead | UnixFileMode.UserExecute : UnixFileMode.UserRead);
    internal override void RemoveTree(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A cache directory cannot be a symbolic link.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0) RemoveTree(child);
            else if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(child);
            else File.Delete(child);
        }
        Directory.Delete(path);
    }
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
        try { return LinuxFileLease.AdoptShared(inherited, (LinuxPrivateDirectory)leases, digest + ".lock"); }
        catch { inherited.Dispose(); throw; }
    }
}
