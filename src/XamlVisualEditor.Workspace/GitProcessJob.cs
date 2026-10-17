using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace XamlVisualEditor.Workspace;

/// <summary>
/// A Windows job object that holds a git process and every process it starts, also a process whose parent already
/// ended, as with the processes a hook starts through the MSYS shell. Processes started by a process of the job
/// belong to the job, because the job does not allow breaking away. While the job is open, its processes end when the
/// editor ends (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>). <see cref="Release"/> lifts that limit before it closes
/// the job, so processes that a finished git command leaves behind on purpose, such as the file system monitor
/// daemon, keep running. Only for Windows.
/// </summary>
internal sealed class GitProcessJob : IDisposable
{
    private const int JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint TERMINATED_EXIT_CODE = 1;

    private readonly JobHandle _handle;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    private GitProcessJob(JobHandle handle, ILogger logger)
    {
        _handle = handle;
        _logger = logger;
    }

    /// <summary>
    /// Creates a job and assigns <paramref name="process"/> to it; returns null and logs why when that fails, for
    /// example because the process already ended.
    /// </summary>
    public static GitProcessJob? TryAssign(Process process, ILogger logger)
    {
        JobHandle handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            logger.LogWarning("CreateJobObject failed with error {Error}; cancelling git kills its process tree only.", error);
            return null;
        }

        if (!SetLimitFlags(handle, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)
            || !AssignProcessToJobObject(handle, process.SafeHandle))
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            logger.LogDebug(
                "git process {ProcessId} could not be assigned to a job (error {Error}); cancelling it kills its process tree only.",
                process.Id,
                error);
            return null;
        }

        return new GitProcessJob(handle, logger);
    }

    /// <summary>Kills every process of the job; returns false when the job is closed or could not be terminated.</summary>
    public bool Terminate()
    {
        lock (_sync)
        {
            if (_handle.IsClosed)
            {
                return false;
            }

            if (TerminateJobObject(_handle, TERMINATED_EXIT_CODE))
            {
                return true;
            }

            _logger.LogWarning("TerminateJobObject failed with error {Error}.", Marshal.GetLastPInvokeError());
            return false;
        }
    }

    /// <summary>Closes the job without killing the processes that are still in it.</summary>
    public void Release()
    {
        lock (_sync)
        {
            if (_handle.IsClosed)
            {
                return;
            }

            if (!SetLimitFlags(_handle, 0))
            {
                _logger.LogWarning(
                    "The kill-on-close limit of a git job could not be lifted (error {Error}); its remaining processes end now.",
                    Marshal.GetLastPInvokeError());
            }

            _handle.Dispose();
        }
    }

    /// <summary>Closes the job; while the kill-on-close limit is set, this kills the processes still in it.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _handle.Dispose();
        }
    }

    private static bool SetLimitFlags(JobHandle handle, uint limitFlags)
    {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION information = new()
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = limitFlags }
        };
        return SetInformationJobObject(
            handle,
            JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS,
            ref information,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern JobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        JobHandle hJob,
        int jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(JobHandle hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(JobHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
