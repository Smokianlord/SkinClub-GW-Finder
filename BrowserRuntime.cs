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
        public const int MaxProcesses = 5;
        private static readonly object Gate = new object();
        private static readonly CancellationTokenSource ShutdownSource = new CancellationTokenSource();
        private static IntPtr activeJob;
        private static Process activeRoot;
        private static Timer watchdog;
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

        private static int CountLocked()
        {
            if (activeJob == IntPtr.Zero) return 0;
            Accounting info;
            if (!QueryInformationJobObject(activeJob, 1, out info, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify browser process count");
            return (int)info.ActiveProcesses;
        }
        public static int ActiveProcessCount { get { lock (Gate) { return CountLocked(); } } }

        // Never run an uncontained browser. Assignment happens before its first instruction,
        // so every descendant inherits this per-page job, including if the root exits first.
        public static Process StartHidden(string executable, string arguments)
        {
            lock (Gate)
            {
                if (shuttingDown) throw new OperationCanceledException("Application is closing");
                if (activeJob != IntPtr.Zero) throw new InvalidOperationException("Previous browser cleanup has not completed");
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                ProcessInformation native = new ProcessInformation();
                Process root = null;
                bool started = false;
                try
                {
                    ExtendedLimits limits = new ExtendedLimits();
                    // KILL_ON_JOB_CLOSE | ACTIVE_PROCESS; no breakaway flags.
                    limits.Basic.LimitFlags = 0x2000 | 0x8;
                    limits.Basic.ActiveProcessLimit = MaxProcesses;
                    if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    StartupInfo startup = new StartupInfo();
                    startup.Size = (uint)Marshal.SizeOf(typeof(StartupInfo));
                    startup.Flags = 1; // STARTF_USESHOWWINDOW
                    startup.ShowWindow = 0;
                    if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" " + arguments),
                        IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, null, ref startup, out native))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!AssignProcessToJobObject(job, native.Process))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Browser containment failed; launch cancelled");
                    root = Process.GetProcessById((int)native.Pid);
                    activeJob = job;
                    activeRoot = root;
                    if (ResumeThread(native.Thread) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    // An independent backstop also kills the job if async rendering gets stuck.
                    watchdog = new Timer(delegate
                    {
                        lock (Gate)
                        {
                            if (activeJob != job) return;
                            ActivityLog.Write("TIMEOUT", "Browser exceeded 45 seconds; terminating its entire process group");
                            if (!TerminateJobObject(job, 1)) ActivityLog.Write("ERROR", "Watchdog cleanup failed: " + Marshal.GetLastWin32Error());
                        }
                    }, null, 45000, Timeout.Infinite);
                    started = true;
                    ActivityLog.Write("BROWSER", "Started PID " + native.Pid + "; hard process cap " + MaxProcesses + "; one page at a time");
                    return root;
                }
                finally
                {
                    if (!started)
                    {
                        if (native.Process != IntPtr.Zero) TerminateProcess(native.Process, 1);
                        TerminateJobObject(job, 1);
                        CloseHandle(job);
                        activeJob = IntPtr.Zero;
                        activeRoot = null;
                        if (root != null) root.Dispose();
                    }
                    if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
                    if (native.Process != IntPtr.Zero) CloseHandle(native.Process);
                }
            }
        }

        public static void KillProcessTree(Process process)
        {
            if (process == null) return;
            lock (Gate)
            {
                if (!object.ReferenceEquals(process, activeRoot)) return;
                int pid = process.Id;
                if (watchdog != null) { watchdog.Dispose(); watchdog = null; }
                if (!TerminateJobObject(activeJob, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate browser group; new launches blocked");
                Stopwatch wait = Stopwatch.StartNew();
                while (CountLocked() != 0 && wait.ElapsedMilliseconds < 5000) Thread.Sleep(25);
                if (CountLocked() != 0)
                    throw new InvalidOperationException("Browser children are still exiting; new launches blocked");
                CloseHandle(activeJob);
                activeJob = IntPtr.Zero;
                activeRoot = null;
                process.Dispose();
                ActivityLog.Write("CLEANUP", "PID " + pid + " and all children closed; browser processes 0/" + MaxProcesses);
            }
        }

        public static void Shutdown()
        {
            ShutdownSource.Cancel();
            lock (Gate)
            {
                if (shuttingDown) return;
                shuttingDown = true;
                if (watchdog != null) { watchdog.Dispose(); watchdog = null; }
                if (activeJob != IntPtr.Zero)
                {
                    TerminateJobObject(activeJob, 0);
                    CloseHandle(activeJob);
                    activeJob = IntPtr.Zero;
                }
                if (activeRoot != null) { activeRoot.Dispose(); activeRoot = null; }
            }
        }
    }
}
