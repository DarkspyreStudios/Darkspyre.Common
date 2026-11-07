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

    /// <summary>A host that runs as a person keeps one entry for the person and one for SYSTEM, and is accepted.</summary>
    [Fact]
    public void AListOfAHostRunningAsAPersonHoldsTwoEntriesAndIsAccepted()
    {
        if (!OperatingSystem.IsWindows()) return;
        RawSecurityDescriptor expected = Descriptor(PersonSid());

        Assert.Equal(2, expected.DiscretionaryAcl!.Count);
        WindowsFileSecurity.Verify(Descriptor(PersonSid()), expected);
    }

    private static SecurityIdentifier SystemSid() => new(WellKnownSidType.LocalSystemSid, null);

    private static SecurityIdentifier PersonSid() => new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    // The descriptor the driver builds for a private service directory owned by one account.
    private static RawSecurityDescriptor Descriptor(SecurityIdentifier user) =>
        new(WindowsFileSecurity.Create(user, directory: true).GetSecurityDescriptorBinaryForm(), 0);
}
