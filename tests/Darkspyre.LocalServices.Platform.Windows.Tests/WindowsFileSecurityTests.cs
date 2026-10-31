using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace Darkspyre.LocalServices.Platform.Windows.Tests;

/// <summary>
/// The private access control list a service resource carries, and the check that refuses any other list. Every check
/// here runs on Windows only: <see cref="SecurityIdentifier"/> throws <see cref="PlatformNotSupportedException"/> on
/// every other operating system, so each one returns at once elsewhere and proves nothing there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileSecurityTests
{
    private const string Refusal = "Service resources require a protected DACL for the current user and SYSTEM.";

    /// <summary>
    /// A headless Windows Harness runs as LocalSystem, so the rule for the user and the rule for SYSTEM name one SID
    /// and the list holds one entry. That list is the correct one for such a host and is accepted.
    /// </summary>
    [Fact]
    public void AListOfAHostRunningAsSystemHoldsOneEntryAndIsAccepted()
    {
        if (!OperatingSystem.IsWindows()) return;
        RawSecurityDescriptor expected = Descriptor(SystemSid());

        Assert.Equal(1, expected.DiscretionaryAcl!.Count);
        WindowsFileSecurity.Verify(Descriptor(SystemSid()), expected);
    }

    /// <summary>A host that runs as a person keeps one entry for the person and one for SYSTEM, and is accepted.</summary>
    [Fact]
    public void AListOfAHostRunningAsAPersonHoldsTwoEntriesAndIsAccepted()
    {
        if (!OperatingSystem.IsWindows()) return;
        RawSecurityDescriptor expected = Descriptor(PersonSid());

        Assert.Equal(2, expected.DiscretionaryAcl!.Count);
        WindowsFileSecurity.Verify(Descriptor(PersonSid()), expected);
    }

    /// <summary>
    /// A list with an entry the expected list does not hold is refused, so a host running as LocalSystem still refuses
    /// a folder another account may reach.
    /// </summary>
    [Fact]
    public void AListWithAnEntryBeyondTheExpectedOneIsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        FileSystemSecurity widened = WindowsFileSecurity.Create(SystemSid(), directory: true);
        widened.AddAccessRule(new FileSystemAccessRule(PersonSid(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        RawSecurityDescriptor actual = new(widened.GetSecurityDescriptorBinaryForm(), 0);

        UnauthorizedAccessException refused = Assert.Throws<UnauthorizedAccessException>(
            () => WindowsFileSecurity.Verify(actual, Descriptor(SystemSid())));

        Assert.Equal(Refusal, refused.Message);
    }

    private static SecurityIdentifier SystemSid() => new(WellKnownSidType.LocalSystemSid, null);

    private static SecurityIdentifier PersonSid() => new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    // The descriptor the driver builds for a private service directory owned by one account.
    private static RawSecurityDescriptor Descriptor(SecurityIdentifier user) =>
        new(WindowsFileSecurity.Create(user, directory: true).GetSecurityDescriptorBinaryForm(), 0);
}
