using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Native;

namespace HowardLab.EbayCrm.AppHost.Windows.Control;

internal static unsafe class NativeNamedPipeServerFactory
{
    internal static NamedPipeServerStream Create(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        using var securityDescriptor = NativeSecurityDescriptor.CreateForCurrentUserAndLogon();
        var attributes = new NativeMethods.SecurityAttributes
        {
            Length = checked((uint)sizeof(NativeMethods.SecurityAttributes)),
            SecurityDescriptor = securityDescriptor.DangerousGetHandle().ToPointer(),
            InheritHandle = 0,
        };
        var handle = NativeMethods.CreateNamedPipe(
            $"\\\\.\\pipe\\{pipeName}",
            NativeMethods.PipeAccessDuplex |
                NativeMethods.FileFlagOverlapped |
                NativeMethods.FileFlagFirstPipeInstance,
            NativeMethods.PipeTypeByte |
                NativeMethods.PipeReadModeByte |
                NativeMethods.PipeWait |
                NativeMethods.PipeRejectRemoteClients,
            maxInstances: 1,
            outBufferSize: ControlProtocolConstants.MaxFrameBytes,
            inBufferSize: ControlProtocolConstants.MaxFrameBytes,
            defaultTimeout: 0,
            &attributes);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            return new NamedPipeServerStream(
                PipeDirection.InOut,
                isAsync: true,
                isConnected: false,
                handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
