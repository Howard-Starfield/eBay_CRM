using System.Security.AccessControl;
using System.Security.Principal;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Payload;

public sealed class TrustedApplicationRootSecurityTests
{
    private const int GenericRead = unchecked((int)0x80000000);
    private const int GenericWrite = 0x40000000;
    private const int GenericExecute = 0x20000000;
    private const int GenericAll = 0x10000000;

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, domainSid: null);

    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static readonly SecurityIdentifier OrdinaryUser =
        new("S-1-5-21-1000-1001-1002-1003");

    public static TheoryData<SecurityIdentifier> TrustedOwners() =>
        new()
        {
            Administrators,
            LocalSystem,
            TrustedInstaller,
        };

    [Theory]
    [MemberData(nameof(TrustedOwners))]
    public void Validate_TrustedOwnerWithProtectedDacl_Passes(SecurityIdentifier owner)
    {
        var descriptor = Descriptor(owner, Allowed(OrdinaryUser, FileSystemRights.ReadAndExecute));

        TrustedApplicationRootSecurity.Validate(descriptor);
    }

    [Fact]
    public void Validate_UntrustedOwner_FailsClosed() =>
        AssertTrustFailure(Descriptor(OrdinaryUser, Allowed(OrdinaryUser, FileSystemRights.ReadAndExecute)));

    [Fact]
    public void Validate_NullDacl_FailsClosed() =>
        AssertTrustFailure(Descriptor(Administrators, dacl: null));

    [Fact]
    public void Validate_UnprotectedDacl_FailsClosed() =>
        AssertTrustFailure(Descriptor(
            Administrators,
            Allowed(OrdinaryUser, FileSystemRights.ReadAndExecute),
            protectedDacl: false));

    [Fact]
    public void Validate_OrdinaryReadExecuteAndGenericReadExecute_Passes()
    {
        var descriptor = Descriptor(
            Administrators,
            Allowed(
                OrdinaryUser,
                (int)FileSystemRights.ReadAndExecute | GenericRead | GenericExecute));

        TrustedApplicationRootSecurity.Validate(descriptor);
    }

    public static TheoryData<int> OrdinaryMutationMasks() =>
        new()
        {
            (int)FileSystemRights.WriteData,
            (int)FileSystemRights.AppendData,
            (int)FileSystemRights.WriteExtendedAttributes,
            (int)FileSystemRights.DeleteSubdirectoriesAndFiles,
            (int)FileSystemRights.WriteAttributes,
            (int)FileSystemRights.Delete,
            (int)FileSystemRights.ChangePermissions,
            (int)FileSystemRights.TakeOwnership,
            (int)FileSystemRights.Modify,
            (int)FileSystemRights.FullControl,
            GenericWrite,
            GenericAll,
        };

    [Theory]
    [MemberData(nameof(OrdinaryMutationMasks))]
    public void Validate_OrdinaryMutationRight_FailsClosed(int mutationMask) =>
        AssertTrustFailure(Descriptor(Administrators, Allowed(OrdinaryUser, mutationMask)));

    [Fact]
    public void Validate_OrdinaryUnknownAccessMaskBit_FailsClosed() =>
        AssertTrustFailure(Descriptor(Administrators, Allowed(OrdinaryUser, 0x00000400)));

    public static TheoryData<SecurityIdentifier> PrivilegedTrustees() => TrustedOwners();

    [Theory]
    [MemberData(nameof(PrivilegedTrustees))]
    public void Validate_PrivilegedTrusteeMayMutate(SecurityIdentifier trustee)
    {
        var descriptor = Descriptor(Administrators, Allowed(trustee, GenericAll));

        TrustedApplicationRootSecurity.Validate(descriptor);
    }

    [Fact]
    public void Validate_StandardExplicitDenyAce_Passes()
    {
        var descriptor = Descriptor(
            Administrators,
            new CommonAce(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                AceQualifier.AccessDenied,
                GenericAll,
                OrdinaryUser,
                isCallback: false,
                opaque: null));

        TrustedApplicationRootSecurity.Validate(descriptor);
    }

    [Fact]
    public void Validate_InheritedAce_FailsClosed() =>
        AssertTrustFailure(Descriptor(
            Administrators,
            new CommonAce(
                AceFlags.Inherited,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.ReadAndExecute,
                OrdinaryUser,
                isCallback: false,
                opaque: null)));

    [Fact]
    public void Validate_CallbackAce_FailsClosed() =>
        AssertTrustFailure(Descriptor(
            Administrators,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.ReadAndExecute,
                OrdinaryUser,
                isCallback: true,
                opaque: [1, 2, 3, 4])));

    [Fact]
    public void Validate_ObjectAce_FailsClosed() =>
        AssertTrustFailure(Descriptor(
            Administrators,
            new ObjectAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.ReadAndExecute,
                OrdinaryUser,
                ObjectAceFlags.None,
                type: Guid.Empty,
                inheritedType: Guid.Empty,
                isCallback: false,
                opaque: null)));

    [Fact]
    public void Validate_UnknownAceShape_FailsClosed() =>
        AssertTrustFailure(Descriptor(
            Administrators,
            new CompoundAce(
                AceFlags.None,
                GenericAll,
                CompoundAceType.Impersonation,
                Administrators)));

    [Fact]
    public void Validate_NullDescriptor_HasOnlySanitizedFailureSurface()
    {
        var error = Assert.Throws<NodePayloadManifestException>(() =>
            TrustedApplicationRootSecurity.Validate(null!));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Equal("role-payload-trust-failed", error.Message);
        Assert.Null(error.InnerException);
    }

    private static CommonAce Allowed(
        SecurityIdentifier trustee,
        FileSystemRights rights) => Allowed(trustee, (int)rights);

    private static CommonAce Allowed(SecurityIdentifier trustee, int accessMask) =>
        new(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            accessMask,
            trustee,
            isCallback: false,
            opaque: null);

    private static RawSecurityDescriptor Descriptor(
        SecurityIdentifier owner,
        GenericAce? ace = null,
        bool protectedDacl = true,
        bool dacl = true) => Descriptor(owner, dacl ? BuildDacl(ace) : null, protectedDacl);

    private static RawSecurityDescriptor Descriptor(
        SecurityIdentifier owner,
        RawAcl? dacl,
        bool protectedDacl = true)
    {
        var flags = ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent;
        if (protectedDacl)
        {
            flags |= ControlFlags.DiscretionaryAclProtected;
        }

        return new RawSecurityDescriptor(
            flags,
            owner,
            group: null,
            systemAcl: null,
            discretionaryAcl: dacl);
    }

    private static RawAcl BuildDacl(GenericAce? ace)
    {
        var dacl = new RawAcl(GenericAcl.AclRevision, ace is null ? 0 : 1);
        if (ace is not null)
        {
            dacl.InsertAce(0, ace);
        }

        return dacl;
    }

    private static void AssertTrustFailure(RawSecurityDescriptor descriptor)
    {
        var error = Assert.Throws<NodePayloadManifestException>(() =>
            TrustedApplicationRootSecurity.Validate(descriptor));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Equal("role-payload-trust-failed", error.Message);
        Assert.Null(error.InnerException);
    }
}
