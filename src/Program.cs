using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("SkinClub GW Finder")]
[assembly: AssemblyProduct("SkinClub GW Finder")]
[assembly: AssemblyDescription("SkinClub creator giveaway monitor")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]
[assembly: AssemblyInformationalVersion("1.4.0")]


namespace SkinClubGiveawayDesktop
{
    static class Program
    {
        internal static readonly string DisplayTitle = "SkinClub GW Finder v" + typeof(Program).Assembly.GetName().Version.ToString(3);

        [STAThread]
        static void Main()
        {
            // Two independent shutdown hooks plus the Job Object itself make browser
            // cleanup reliable for normal close, managed shutdown, and most crashes.
            Application.ApplicationExit += delegate { BrowserProcessManager.Shutdown(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { BrowserProcessManager.Shutdown(); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                BrowserProcessManager.Shutdown();
            }
        }
    }
}
