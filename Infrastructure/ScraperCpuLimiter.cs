using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShippingManagement.Web.Infrastructure
{
    /// <summary>
    /// Hard-caps the CPU usage of a scraper process AND every child it spawns
    /// (all the Chrome/Chromium processes) using a Windows Job Object with
    /// CPU-rate control, and drops its scheduling priority to BelowNormal so
    /// IIS / SQL Server always win CPU contention.
    ///
    ///  • The cap is enforced by the Windows kernel across the WHOLE process
    ///    tree — e.g. 70 means the python + all Chrome children together can
    ///    never exceed 70% of total machine CPU, no matter how many workers run.
    ///  • Child processes are placed into the job automatically, so nothing in
    ///    the Python scripts needs to change.
    ///  • KILL_ON_JOB_CLOSE is also set: if the web app dies mid-scrape, every
    ///    orphaned Chrome is killed with it (no more zombie chrome.exe piles).
    ///
    /// Requires Windows 8 / Server 2012 or newer. On failure (or non-Windows)
    /// it silently degrades to just lowering the process priority.
    ///
    /// ONE limiter is shared by BOTH scraper processes (MyShipTracking +
    /// VesselTracker) so the cap applies to their COMBINED usage — two separate
    /// 70% caps would still let them reach 100% together.
    ///
    /// Usage:
    ///     using var limiter = ScraperCpuLimiter.Create(maxCpuPercent: 70);
    ///     ... limiter.Attach(proc1); limiter.Attach(proc2); ...
    ///     // keep 'limiter' alive until the processes exit
    /// </summary>
    public sealed class ScraperCpuLimiter : IDisposable
    {
        private IntPtr _job = IntPtr.Zero;

        private ScraperCpuLimiter() { }

        /// <summary>
        /// Create one job object capped at <paramref name="maxCpuPercent"/> of
        /// total machine CPU. Never throws — on any failure it returns an inert
        /// limiter and scraping proceeds unthrottled (priority still applied).
        /// </summary>
        public static ScraperCpuLimiter Create(int maxCpuPercent)
        {
            var limiter = new ScraperCpuLimiter();

            if (!OperatingSystem.IsWindows()) return limiter;
            if (maxCpuPercent <= 0 || maxCpuPercent >= 100) return limiter; // 0/100 = unlimited

            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return limiter;

                // Kill every process in the job when the last handle closes
                // (cleanup of orphaned Chromes if the app pool recycles).
                var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                ext.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!SetInformationJobObject(job, JobObjectInfoClass.ExtendedLimitInformation,
                                             ref ext, Marshal.SizeOf(ext)))
                { CloseHandle(job); return limiter; }

                // The actual CPU cap. CpuRate is in 1/100ths of a percent of
                // TOTAL machine CPU: 70% → 7000.
                var rate = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE
                                 | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                    CpuRate = (uint)(maxCpuPercent * 100)
                };
                if (!SetInformationJobObject(job, JobObjectInfoClass.CpuRateControlInformation,
                                             ref rate, Marshal.SizeOf(rate)))
                { CloseHandle(job); return limiter; }

                limiter._job = job;   // keep the handle alive until Dispose
            }
            catch { /* throttling must never break the scrape */ }

            return limiter;
        }

        /// <summary>
        /// Attach one scraper process to the shared job (Chrome children follow
        /// automatically — they don't use CREATE_BREAKAWAY) and drop it to
        /// BelowNormal priority (children inherit BelowNormal on Windows).
        /// </summary>
        public void Attach(Process proc)
        {
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { /* best effort */ }

            if (_job == IntPtr.Zero) return;
            try { AssignProcessToJobObject(_job, proc.Handle); }
            catch { /* best effort */ }
        }

        public void Dispose()
        {
            if (_job != IntPtr.Zero)
            {
                // NOTE: because of KILL_ON_JOB_CLOSE, close this AFTER the
                // process has exited (RunScraper's flow already guarantees it).
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }

        // ───────────────────────── P/Invoke plumbing ─────────────────────────

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE   = 0x00002000;
        private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE   = 0x00000001;
        private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x00000004;

        private enum JobObjectInfoClass
        {
            ExtendedLimitInformation = 9,
            CpuRateControlInformation = 15
        }

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
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;      // union member: rate in 1/100ths of a percent
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, JobObjectInfoClass infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, JobObjectInfoClass infoClass,
            ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
