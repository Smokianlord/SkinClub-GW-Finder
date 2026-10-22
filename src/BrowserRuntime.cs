using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SkinClubGiveawayDesktop
{
    // Bounded, thread-safe session log. UI polls it, so workers never block on UI invokes.
    public static class ActivityLog
    {
        private static readonly object Gate = new object();
        private static readonly Queue<string> Lines = new Queue<string>();
        private static long version;
        public static void Write(string category, string message)
        {
            string clean = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (clean.Length > 500) clean = clean.Substring(0, 500);
            lock (Gate)
            {
                Lines.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  [" + category + "]  " + clean);
                while (Lines.Count > 2000) Lines.Dequeue();
                version++;
            }
        }
        public static string Snapshot(ref long seen)
        {
            lock (Gate)
            {
                if (seen == version) return null;
                seen = version;
                return string.Join(Environment.NewLine, Lines.ToArray()) + Environment.NewLine;
            }
        }
        public static void Clear() { lock (Gate) { Lines.Clear(); version++; } }
    }

    public static class BrowserProcessManager
    {
        // Processes allowed per browser (root plus children), and how many browsers run at once.
        public static int MaxProcesses { get { return AppSettings.Current.BrowserProcessLimit; } }
        public static int MaxConcurrent { get { return AppSettings.Current.BrowserConcurrency; } }
        public static int TotalProcessCap { get { return MaxProcesses * MaxConcurrent; } }

        private sealed class BrowserJob
        {
            public IntPtr Job;
            public Process Root;
            public Timer Watchdog;
        }

        private static readonly object Gate = new object();
        private static readonly CancellationTokenSource ShutdownSource = new CancellationTokenSource();
        private static readonly List<BrowserJob> Jobs = new List<BrowserJob>();
        private static bool shuttingDown;
        public static CancellationToken ShutdownToken { get { return ShutdownSource.Token; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Accounting
        {
            public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public uint Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedSize;
            public IntPtr ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr Process, Thread; public uint Pid, Tid; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int kind, out Accounting info, uint size, IntPtr length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static int lastCount;

        private static int CountJobLocked(BrowserJob job)
        {
            Accounting info;
            if (!QueryInformationJobObject(job.Job, 1, out info, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify browser process count");
            return (int)info.ActiveProcesses;
        }

        private static int CountLocked()
        {
            int total = 0;
            foreach (BrowserJob job in Jobs) total += CountJobLocked(job);
            lastCount = total;
            return total;
        }

        // Called by the UI timer. Starting a browser holds Gate briefly; the UI must never freeze
        // behind it, so fall back to the last count read.
        public static int ActiveProcessCount
        {
            get
            {
                bool taken = false;
                try
                {
                    Monitor.TryEnter(Gate, 100, ref taken);
                    return taken ? CountLocked() : Volatile.Read(ref lastCount);
                }
                finally { if (taken) Monitor.Exit(Gate); }
            }
        }

        // Never run an uncontained browser. Assignment happens before its first instruction,
        // so every descendant inherits this per-page job, including if the root exits first.
        public static Process StartHidden(string executable, string arguments)
        {
            lock (Gate)
            {
                if (shuttingDown) throw new OperationCanceledException("Application is closing");
                if (Jobs.Count >= MaxConcurrent) throw new InvalidOperationException("Previous browser cleanup has not completed");
                IntPtr handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                BrowserJob job = new BrowserJob { Job = handle };
                ProcessInformation native = new ProcessInformation();
                Process root = null;
                bool started = false;
                try
                {
                    ExtendedLimits limits = new ExtendedLimits();
                    // KILL_ON_JOB_CLOSE | ACTIVE_PROCESS | JOB_MEMORY; no breakaway flags.
                    // The memory budget is shared between the browsers that may run at once.
                    limits.Basic.LimitFlags = 0x2000 | 0x8 | 0x200;
                    limits.Basic.ActiveProcessLimit = (uint)MaxProcesses;
                    ulong memoryBytes = (ulong)Math.Max(256, AppSettings.Current.BrowserMemoryLimitMB / MaxConcurrent) * 1024UL * 1024UL;
                    if (UIntPtr.Size == 4) memoryBytes = Math.Min(memoryBytes, uint.MaxValue);
                    limits.JobMemory = (UIntPtr)memoryBytes;
                    if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    StartupInfo startup = new StartupInfo();
                    startup.Size = (uint)Marshal.SizeOf(typeof(StartupInfo));
                    startup.Flags = 1; // STARTF_USESHOWWINDOW
                    startup.ShowWindow = 0;
                    if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" " + arguments),
                        IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, null, ref startup, out native))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!AssignProcessToJobObject(handle, native.Process))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Browser containment failed; launch cancelled");
                    root = Process.GetProcessById((int)native.Pid);
                    job.Root = root;
                    Jobs.Add(job);
                    if (ResumeThread(native.Thread) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    // An independent backstop also kills the job if async rendering gets stuck.
                    job.Watchdog = new Timer(delegate { WatchdogFired(job); }, null, 45000, Timeout.Infinite);
                    started = true;
                    ActivityLog.Write("BROWSER", "Started PID " + native.Pid + "; up to " + MaxProcesses + " processes and " +
                        Math.Max(256, AppSettings.Current.BrowserMemoryLimitMB / MaxConcurrent) + " MB each; " + Jobs.Count + " of " + MaxConcurrent + " browsers running");
                    return root;
                }
                finally
                {
                    if (!started)
                    {
                        if (native.Process != IntPtr.Zero) TerminateProcess(native.Process, 1);
                        TerminateJobObject(handle, 1);
                        CloseHandle(handle);
                        Jobs.Remove(job);
                        if (root != null) root.Dispose();
                    }
                    if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
                    if (native.Process != IntPtr.Zero) CloseHandle(native.Process);
                }
            }
        }

        private static void WatchdogFired(BrowserJob job)
        {
            lock (Gate)
            {
                if (!Jobs.Contains(job)) return;
                ActivityLog.Write("TIMEOUT", "Browser exceeded 45 seconds; terminating its entire process group");
                if (!TerminateJobObject(job.Job, 1)) ActivityLog.Write("ERROR", "Watchdog cleanup failed: " + Marshal.GetLastWin32Error());
            }
        }

        // Test hook: shorten the watchdog of one running browser.
        internal static void SetWatchdogDueTime(Process root, int milliseconds)
        {
            lock (Gate)
            {
                foreach (BrowserJob job in Jobs)
                    if (object.ReferenceEquals(job.Root, root) && job.Watchdog != null) job.Watchdog.Change(milliseconds, Timeout.Infinite);
            }
        }

        public static void KillProcessTree(Process process)
        {
            if (process == null) return;
            BrowserJob job = null;
            lock (Gate)
            {
                foreach (BrowserJob candidate in Jobs)
                    if (object.ReferenceEquals(candidate.Root, process)) { job = candidate; break; }
                if (job == null) return;
                if (job.Watchdog != null) { job.Watchdog.Dispose(); job.Watchdog = null; }
                if (!TerminateJobObject(job.Job, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate browser group; its launch slot stays blocked");
            }

            // Wait for the group to empty without holding Gate, so other browsers keep running and
            // the UI is never blocked. The job stays registered, which keeps its slot occupied.
            int pid = process.Id;
            Stopwatch wait = Stopwatch.StartNew();
            int remaining;
            do
            {
                lock (Gate)
                {
                    if (!Jobs.Contains(job)) return; // Shutdown already closed everything
                    remaining = CountJobLocked(job);
                }
                if (remaining != 0) Thread.Sleep(25);
            } while (remaining != 0 && wait.ElapsedMilliseconds < 5000);
            if (remaining != 0)
                throw new InvalidOperationException("Browser children are still exiting; its launch slot stays blocked");

            lock (Gate)
            {
                if (!Jobs.Contains(job)) return;
                CloseHandle(job.Job);
                Jobs.Remove(job);
                process.Dispose();
                ActivityLog.Write("CLEANUP", "PID " + pid + " and all children closed; browsers running " + Jobs.Count + "/" + MaxConcurrent);
            }
        }

        public static void Shutdown()
        {
            ShutdownSource.Cancel();
            lock (Gate)
            {
                if (shuttingDown) return;
                shuttingDown = true;
                foreach (BrowserJob job in Jobs)
                {
                    if (job.Watchdog != null) { job.Watchdog.Dispose(); job.Watchdog = null; }
                    TerminateJobObject(job.Job, 0);
                    CloseHandle(job.Job);
                    if (job.Root != null) job.Root.Dispose();
                }
                Jobs.Clear();
            }
        }
    }
}