using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Windows;

internal static class WindowsFileSecurity
{
    internal static SecurityIdentifier User
    {
        get { using WindowsIdentity identity = WindowsIdentity.GetCurrent(); return identity.User ?? throw new UnauthorizedAccessException("The current process has no user SID."); }
    }
    internal static FileSystemSecurity Create(bool directory, bool readOnly = false, bool executable = false) =>
        Create(User, directory, readOnly, executable);
    // The given account owns the resource and holds one allow rule, and SYSTEM holds the other. A host that runs as
    // SYSTEM gives both rules the same SID, so the access control list keeps one entry instead of two.
    internal static FileSystemSecurity Create(SecurityIdentifier user, bool directory, bool readOnly = false, bool executable = false)
    {
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        FileSystemRights rights = !readOnly ? FileSystemRights.FullControl : directory || executable ? FileSystemRights.ReadAndExecute : FileSystemRights.Read;
        InheritanceFlags inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(user, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
    internal static void Inspect(SafeFileHandle handle, bool directory, bool readOnly = false, bool executable = false)
    {
        WindowsNative.Inspect(handle, directory);
        uint error = WindowsNative.GetSecurityInfo(handle, 1, 1 | 4, out _, out _, out _, out _, out nint descriptor);
        if (error != 0) throw WindowsNative.Error("read private file security", (int)error);
        try
        {
            byte[] bytes = new byte[WindowsNative.GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var actual = new RawSecurityDescriptor(bytes, 0);
            byte[] expectedBytes = Create(directory, readOnly, executable).GetSecurityDescriptorBinaryForm();
            Verify(actual, new RawSecurityDescriptor(expectedBytes, 0));
        }
        finally { WindowsNative.LocalFree(descriptor); }
    }
    // Refuses a descriptor that is not the one Create builds for this process: another owner, an inheriting access
    // control list, a different number of entries, or an entry whose rights differ. The expected descriptor decides the
    // number of entries, which is one on a host that runs as SYSTEM, because the user and SYSTEM rules name one SID.
    internal static void Verify(RawSecurityDescriptor actual, RawSecurityDescriptor expected)
    {
        if (actual.Owner != expected.Owner || !actual.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            || expected.DiscretionaryAcl is null || actual.DiscretionaryAcl?.Count != expected.DiscretionaryAcl.Count)
            throw new UnauthorizedAccessException("Service resources require a protected DACL for the current user and SYSTEM.");
        // Compare the explicit allow entries, independent of the ACL serialization order.
        foreach (GenericAce ace in expected.DiscretionaryAcl)
        {
            if (ace is not CommonAce rule || rule.AceQualifier != AceQualifier.AccessAllowed
                || actual.DiscretionaryAcl.Cast<GenericAce>().OfType<CommonAce>().Count(e => e.AceQualifier == AceQualifier.AccessAllowed && e.AceFlags == rule.AceFlags && e.SecurityIdentifier == rule.SecurityIdentifier && e.AccessMask == rule.AccessMask) != 1)
                throw new UnauthorizedAccessException("The service resource access rights do not match its private or sealed state.");
        }
    }
}
