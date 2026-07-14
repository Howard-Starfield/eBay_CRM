using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HowardLab.EbayCrm.AppHost.Windows.Native;

internal static unsafe class NativeSecurityDescriptor
{
    internal static SafeLocalAllocHandle CreateForCurrentUserOnly()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        return Create($"O:{userSid}D:P(A;;GA;;;{userSid})");
    }

    internal static SafeLocalAllocHandle CreateForCurrentUserAndLogon()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        var logonSid = GetLogonSid(identity.AccessToken);
        return Create($"D:P(A;;GA;;;{userSid})(A;;GA;;;{logonSid})");
    }

    internal static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
    }

    private static SafeLocalAllocHandle Create(string sddl)
    {
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(
            sddl,
            NativeMethods.SddlRevision1,
            out var rawDescriptor,
            securityDescriptorSize: null))
        {
            var error = Marshal.GetLastPInvokeError();
            if (rawDescriptor != IntPtr.Zero)
            {
                _ = NativeMethods.LocalFree(rawDescriptor);
            }

            throw new Win32Exception(error);
        }

        return new SafeLocalAllocHandle(rawDescriptor);
    }

    private static string GetLogonSid(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token)
    {
        _ = NativeMethods.GetTokenInformation(
            token,
            NativeMethods.TokenGroups,
            tokenInformation: null,
            tokenInformationLength: 0,
            out var length);
        var error = Marshal.GetLastPInvokeError();
        if (length == 0 || error != 122)
        {
            throw new Win32Exception(error);
        }

        var buffer = new byte[checked((int)length)];
        fixed (byte* pointer = buffer)
        {
            if (!NativeMethods.GetTokenInformation(
                token,
                NativeMethods.TokenGroups,
                pointer,
                length,
                out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var header = (NativeMethods.TokenGroupsHeader*)pointer;
            var groups = &header->Groups;
            for (var index = 0u; index < header->GroupCount; index++)
            {
                if ((groups[index].Attributes & NativeMethods.SeGroupLogonId) == NativeMethods.SeGroupLogonId)
                {
                    return new SecurityIdentifier(groups[index].Sid).Value;
                }
            }
        }

        throw new InvalidOperationException("The current Windows token has no logon SID.");
    }
}
