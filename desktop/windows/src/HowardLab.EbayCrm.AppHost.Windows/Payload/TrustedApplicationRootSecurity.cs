using System.Security.AccessControl;
using System.Security.Principal;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public static class TrustedApplicationRootSecurity
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericExecute = 0x20000000;

    private const uint OrdinaryReadExecuteMask =
        (uint)(
            FileSystemRights.ReadData |
            FileSystemRights.ReadExtendedAttributes |
            FileSystemRights.ExecuteFile |
            FileSystemRights.ReadAttributes |
            FileSystemRights.ReadPermissions |
            FileSystemRights.Synchronize) |
        GenericRead |
        GenericExecute;

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, domainSid: null);

    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static void Validate(RawSecurityDescriptor descriptor)
    {
        try
        {
            if (!IsTrusted(descriptor))
            {
                throw Failure();
            }
        }
        catch (NodePayloadManifestException)
        {
            throw;
        }
        catch
        {
            throw Failure();
        }
    }

    private static bool IsTrusted(RawSecurityDescriptor? descriptor)
    {
        if (descriptor is null ||
            descriptor.Owner is not { } owner ||
            !IsPrivileged(owner) ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
            descriptor.DiscretionaryAcl is not { } dacl)
        {
            return false;
        }

        foreach (GenericAce candidate in dacl)
        {
            if (candidate is not CommonAce ace ||
                ace.IsCallback ||
                (ace.AceFlags & AceFlags.Inherited) != 0)
            {
                return false;
            }

            if (ace.AceQualifier == AceQualifier.AccessDenied)
            {
                continue;
            }

            if (ace.AceQualifier != AceQualifier.AccessAllowed)
            {
                return false;
            }

            if (!IsPrivileged(ace.SecurityIdentifier) &&
                ((uint)ace.AccessMask & ~OrdinaryReadExecuteMask) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPrivileged(SecurityIdentifier sid) =>
        Administrators.Equals(sid) ||
        LocalSystem.Equals(sid) ||
        TrustedInstaller.Equals(sid);

    private static NodePayloadManifestException Failure() => new();
}
