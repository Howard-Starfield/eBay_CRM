using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HowardLab.EbayCrm.AppHost.Windows.Native;

internal sealed unsafe class StartupAttributeList : IDisposable
{
    private void* _buffer;

    private StartupAttributeList(void* buffer)
    {
        _buffer = buffer;
    }

    internal IntPtr Pointer => (IntPtr)_buffer;

    internal static StartupAttributeList Create(uint attributeCount)
    {
        nuint size = 0;
        _ = NativeMethods.InitializeProcThreadAttributeList(
            attributeList: null,
            attributeCount,
            flags: 0,
            &size);
        if (size == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var buffer = NativeMemory.AllocZeroed(size);
        if (!NativeMethods.InitializeProcThreadAttributeList(
            buffer,
            attributeCount,
            flags: 0,
            &size))
        {
            var error = Marshal.GetLastPInvokeError();
            NativeMemory.Free(buffer);
            throw new Win32Exception(error);
        }

        return new StartupAttributeList(buffer);
    }

    internal void Add(nuint attribute, IntPtr* handles, int handleCount)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (!NativeMethods.UpdateProcThreadAttribute(
            _buffer,
            flags: 0,
            attribute,
            handles,
            checked((nuint)(handleCount * IntPtr.Size)),
            previousValue: null,
            returnSize: null))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Dispose()
    {
        var buffer = _buffer;
        if (buffer is null)
        {
            return;
        }

        _buffer = null;
        NativeMethods.DeleteProcThreadAttributeList(buffer);
        NativeMemory.Free(buffer);
    }
}
