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
        _ = NativeMethods.GetKernelObjectSecurity(
            mutex,
            NativeMethods.DaclSecurityInformation,
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
                NativeMethods.DaclSecurityInformation,
                pointer,
                needed,
                out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        var descriptor = new RawSecurityDescriptor(buffer, 0);
        var dacl = descriptor.DiscretionaryAcl;
        if (dacl is null || dacl.Count != 1 ||
            dacl[0] is not CommonAce ace ||
            ace.IsInherited ||
            ace.AceQualifier != AceQualifier.AccessAllowed ||
            ace.AccessMask != unchecked((int)NativeMethods.MutexAllAccess))
        {
            return false;
        }

        var expectedSid = new SecurityIdentifier(NativeSecurityDescriptor.GetCurrentUserSid());
        return expectedSid.Equals(ace.SecurityIdentifier);
    }
}
