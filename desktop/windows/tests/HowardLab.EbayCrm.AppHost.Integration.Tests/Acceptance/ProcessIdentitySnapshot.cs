using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

internal sealed class ProcessIdentitySnapshot : IDisposable
{
    private readonly Process _process;

    private ProcessIdentitySnapshot(Process process, int parentProcessId)
    {
        _process = process;
        _ = process.SafeHandle;
        ProcessId = process.Id;
        ParentProcessId = parentProcessId;
        CreationTimeUtc = process.StartTime.ToUniversalTime();
        ImageName = process.ProcessName + ".exe";
    }

    internal int ProcessId { get; }
    internal int ParentProcessId { get; }
    internal DateTime CreationTimeUtc { get; }
    internal string ImageName { get; }
    internal bool HasExited => _process.HasExited;
    internal Task WaitForExitAsync() => _process.WaitForExitAsync();

    internal async Task TerminateIfRunningAsync(TimeSpan timeout)
    {
        try
        {
            if (!HasExited) _process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException) when (HasExited)
        {
            return;
        }

        await WaitForExitAsync().WaitAsync(timeout);
    }

    internal static IReadOnlyList<(int ProcessId, int ParentProcessId)> EnumerateTree(int rootProcessId)
    {
        var all = EnumerateProcesses();
        DateTime rootCreation;
        try
        {
            using var root = Process.GetProcessById(rootProcessId);
            rootCreation = root.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return [];
        }

        var selected = new Dictionary<int, DateTime> { [rootProcessId] = rootCreation };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var item in all)
            {
                if (selected.ContainsKey(item.ProcessId) ||
                    !selected.TryGetValue(item.ParentProcessId, out var parentCreation)) continue;
                try
                {
                    using var child = Process.GetProcessById(item.ProcessId);
                    var childCreation = child.StartTime.ToUniversalTime();
                    if (childCreation < parentCreation) continue;
                    selected.Add(item.ProcessId, childCreation);
                    changed = true;
                }
                catch (Exception error) when (
                    error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return all.Where(item => selected.ContainsKey(item.ProcessId)).ToArray();
    }

    internal static bool TryOpen(int processId, int parentProcessId, out ProcessIdentitySnapshot? snapshot)
    {
        try
        {
            snapshot = new ProcessIdentitySnapshot(Process.GetProcessById(processId), parentProcessId);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            snapshot = null;
            return false;
        }
    }

    internal bool SameIdentityIfReopened()
    {
        try
        {
            using var reopened = Process.GetProcessById(ProcessId);
            return reopened.StartTime.ToUniversalTime() == CreationTimeUtc;
        }
        catch (ArgumentException)
        {
            return HasExited;
        }
    }

    public void Dispose() => _process.Dispose();

    private static IReadOnlyList<(int ProcessId, int ParentProcessId)> EnumerateProcesses()
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entry = new NativeMethods.ProcessEntry32 { Size = checked((uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>()) };
        var result = new List<(int, int)>();
        if (!NativeMethods.Process32First(snapshot, ref entry)) return result;
        do
        {
            result.Add((checked((int)entry.ProcessId), checked((int)entry.ParentProcessId)));
            entry.Size = checked((uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>());
        }
        while (NativeMethods.Process32Next(snapshot, ref entry));
        return result;
    }

    private static class NativeMethods
    {
        internal const uint Th32csSnapProcess = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry32
        {
            internal uint Size;
            internal uint Usage;
            internal uint ProcessId;
            internal IntPtr DefaultHeapId;
            internal uint ModuleId;
            internal uint Threads;
            internal uint ParentProcessId;
            internal int PriorityClassBase;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);
    }
}
