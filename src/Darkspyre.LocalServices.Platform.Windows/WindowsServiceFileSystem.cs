using System.Globalization;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Windows;

internal sealed class WindowsServiceFileSystem : ServiceFileSystem
{
    internal const string InheritedPinVariable = "DARKSPYRE_SERVICE_PACKAGE_HANDLE";
    internal const string InheritedDigestVariable = "DARKSPYRE_SERVICE_PACKAGE_DIGEST";
    internal const string InheritedCacheVariable = "DARKSPYRE_SERVICE_PACKAGE_CACHE";
    internal override ServiceDirectory OpenPrivateDirectory(string path) => WindowsPrivateDirectory.OpenOrCreate(path);
    internal override SafeFileHandle Open(string path, bool directory) => WindowsPrivateDirectory.OpenPath(path, directory);
    internal override void InspectPrivate(SafeFileHandle handle, bool directory, bool readOnly = false, bool executable = false) => WindowsFileSecurity.Inspect(handle, directory, readOnly, executable);
    internal override bool IsExecutable(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    internal override FileStream CreateFile(string path) => new(WindowsPrivateDirectory.OpenNative(path, WindowsNative.Write, 0, 1, directory: false), FileAccess.Write);
    // FlushFileBuffers also flushes the device write cache, so no separate device flush remains.
    internal override void SyncFile(FileStream file) => file.Flush(flushToDisk: true);
    internal override void FlushDevice(FileStream file) { }
    internal override void CreateDirectory(string path) => new DirectoryInfo(path).Create((DirectorySecurity)WindowsFileSecurity.Create(directory: true));
    internal override void Seal(string path, bool executable)
    {
        bool directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        SetAccess(path, directory, readOnly: true, executable);
    }
    internal override void RemoveTree(string path)
    {
        using (SafeFileHandle handle = Open(path, directory: true)) { }
        SetAccess(path, directory: true);
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            FileAttributes attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A cache payload cannot contain reparse points.");
            if ((attributes & FileAttributes.Directory) != 0) RemoveTree(child);
            else
            {
                SetAccess(child, directory: false);
                File.Delete(child);
            }
        }
        Directory.Delete(path);
    }
    private static void SetAccess(string path, bool directory, bool readOnly = false, bool executable = false)
    {
        // Sealing and collection change the DACL. A sealed owner can change its DACL but has no WRITE_OWNER grant.
        byte[] descriptor = WindowsFileSecurity.Create(directory, readOnly, executable).GetSecurityDescriptorBinaryForm();
        FileSystemSecurity access = directory ? new DirectorySecurity() : new FileSecurity();
        access.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)access);
        else new FileInfo(path).SetAccessControl((FileSecurity)access);
    }
    internal override IServicePackageLease AdoptLaunchLease(ServiceDirectory leases, string digest, string cachePath)
    {
        if (!long.TryParse(Environment.GetEnvironmentVariable(InheritedPinVariable), NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= 0
            || Environment.GetEnvironmentVariable(InheritedDigestVariable) != digest || Environment.GetEnvironmentVariable(InheritedCacheVariable) != cachePath)
            throw new InvalidDataException("The service launch did not supply the expected inherited package lease.");
        Environment.SetEnvironmentVariable(InheritedPinVariable, null);
        Environment.SetEnvironmentVariable(InheritedDigestVariable, null);
        Environment.SetEnvironmentVariable(InheritedCacheVariable, null);
        var handle = new SafeFileHandle(checked((nint)value), ownsHandle: true);
        try { return WindowsFileLease.AdoptShared(handle, (WindowsPrivateDirectory)leases, digest + ".lock"); }
        catch { handle.Dispose(); throw; }
    }
}
