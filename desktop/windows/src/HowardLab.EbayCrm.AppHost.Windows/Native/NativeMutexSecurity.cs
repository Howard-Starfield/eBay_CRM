using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Native;

internal static unsafe class NativeMutexSecurity
{
    internal static bool IsCurrentUserOnly(SafeWaitHandle mutex)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        var requestedInformation =
            NativeMethods.OwnerSecurityInformation | NativeMethods.DaclSecurityInformation;
        _ = NativeMethods.GetKernelObjectSecurity(
            mutex,
            requestedInformation,
            securityDescriptor: null,
            length: 0,
            out var needed);
        var error = Marshal.GetLastPInvokeError();
        if (needed == 0 || error != NativeMethods.ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }

        var buffer = new byte[checked((int)needed)];
        fixed (byte* pointer = buffer)
        {
            if (!NativeMethods.GetKernelObjectSecurity(
                mutex,
                requestedInformation,
                pointer,
                needed,
                out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        var descriptor = new RawSecurityDescriptor(buffer, 0);
        var expectedSid = new SecurityIdentifier(NativeSecurityDescriptor.GetCurrentUserSid());
        return IsCurrentUserOnly(descriptor, expectedSid);
    }

    internal static bool IsCurrentUserOnly(
        RawSecurityDescriptor descriptor,
        SecurityIdentifier expectedSid)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(expectedSid);
        var dacl = descriptor.DiscretionaryAcl;
        if (descriptor.Owner is not { } owner ||
            !expectedSid.Equals(owner) ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
            dacl is null ||
            dacl.Count != 1 ||
            dacl[0] is not CommonAce ace ||
            ace.AceFlags != AceFlags.None ||
            ace.IsCallback ||
            ace.AceQualifier != AceQualifier.AccessAllowed ||
            ace.AccessMask != unchecked((int)NativeMethods.MutexAllAccess) ||
            !expectedSid.Equals(ace.SecurityIdentifier))
        {
            return false;
        }

        return true;
    }
}
