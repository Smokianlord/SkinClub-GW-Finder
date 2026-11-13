using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("SkinClub GW Finder")]
[assembly: AssemblyProduct("SkinClub GW Finder")]
[assembly: AssemblyDescription("SkinClub creator giveaway monitor")]
[assembly: AssemblyVersion("2.3.1.0")]
[assembly: AssemblyFileVersion("2.3.1.0")]
[assembly: AssemblyInformationalVersion("2.3.1")]

namespace SkinClubGiveawayDesktop
{
    static class Program
    {
        internal static readonly string DisplayTitle = "SkinClub GW Finder v" + typeof(Program).Assembly.GetName().Version.ToString(3);

        private const string MutexName = @"Local\SkinClubGWFinder.SingleInstance";
        private const string ShowEventName = @"Local\SkinClubGWFinder.ShowWindow";

        // The app's own taskbar identity. Without it, Windows groups the window by the exe's path and shows
        // the icon it cached for that path, which can be an old icon long after the exe was updated. With
        // it, the taskbar shows the icon the window itself supplies.
        private const string AppUserModelId = "Smokianlord.SkinClubGWFinder";

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [STAThread]
        static void Main()
        {
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
            catch (Exception ex) { Diag.Swallow("Setting the taskbar identity", ex); }
            // Only one copy may run: two copies would write data.json and launch browsers at the
            // same time. A second launch just asks the running copy to come to the front.
            bool firstInstance;
            using (Mutex mutex = new Mutex(true, MutexName, out firstInstance))
            {
                if (!firstInstance)
                {
                    try { using (EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName)) existing.Set(); }
                    catch (Exception ex) { Diag.Swallow("Signalling the running copy", ex); }
                    return;
                }

                // Unhandled errors are written to crash.log (next to data.json) instead of vanishing.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Diag.ReportCrash("UI thread", e.Exception);
                    MessageBox.Show("Something went wrong, but the app is still running.\n\n" + e.Exception.Message +
                        "\n\nDetails were saved to:\n" + Diag.CrashFile, DisplayTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Diag.ReportCrash("Background thread", e.ExceptionObject as Exception);
                    BrowserProcessManager.Shutdown();
                };

                // Two independent shutdown hooks plus the Job Object itself make browser
                // cleanup reliable for normal close, managed shutdown, and most crashes.
                Application.ApplicationExit += delegate { BrowserProcessManager.Shutdown(); };
                AppDomain.CurrentDomain.ProcessExit += delegate { BrowserProcessManager.Shutdown(); };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    try
                    {
                        MainForm form = new MainForm();
                        RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate
                        {
                            try { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(new Action(form.BringToFrontFromTray)); }
                            catch (Exception ex) { Diag.Swallow("Restoring the window", ex); }
                        }, null, Timeout.Infinite, false);
                        try { Application.Run(form); }
                        finally { wait.Unregister(null); }
                    }
                    finally
                    {
                        BrowserProcessManager.Shutdown();
                    }
                }
            }
        }
    }
}
