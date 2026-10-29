using System.Diagnostics;
using System.Runtime.InteropServices;
using MeowFilm.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Runtime;

/// <summary>Closing the owner's handle also terminates remaining owned descendants.</summary>
public sealed partial class OwnedProcessJob : IDisposable
{
    private readonly JobHandle handle;
    private OwnedProcessJob(JobHandle handle) => this.handle = handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    public sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial JobHandle CreateJobObject(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(JobHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static OwnedProcessJob Attach(Process child)
    {
        var handle = CreateJobObject(0, 0);
        try
        {
            var limits = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = 0x2000 } };
            if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(handle, child.SafeHandle)) throw new BridgeException("CLOSED");
            return new OwnedProcessJob(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    public void Dispose() => handle.Dispose();
}
