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

namespace SkinClubGiveawayDesktop
{
    public class GiveawayItem
    {
        public string Creator { get; set; }
        public string Url { get; set; }
        public string Status { get; set; }
        public string Ticket { get; set; }
        public string PromoCode { get; set; }
        public string MinimumDeposit { get; set; }
        public string Deadline { get; set; }
        public string LastChecked { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
        public bool Joined { get; set; }
        public string JoinedAt { get; set; }
        public string HistorySince { get; set; }
        public string RenderedAt { get; set; }
        // Set when a check failed to read the page at all; never saved.
        [System.Web.Script.Serialization.ScriptIgnore]
        public bool Unreachable { get; set; }
    }

    public class AppData
    {
        public List<GiveawayItem> Items { get; set; }
        public string LastScan { get; set; }
        public string LastDeepScan { get; set; }

        public AppData()
        {
            Items = new List<GiveawayItem>();
        }
    }

    public class Candidate
    {
        public string Creator;
        public string Url;
        public string Source;

        public Candidate(string creator, string url, string source)
        {
            Creator = creator;
            Url = url;
            Source = source;
        }
    }

    public class ParseResult
    {
        public string Status;
        public string Ticket;
        public string PromoCode;
        public string MinimumDeposit;
        public string Deadline;
        public int? Remaining;
        public int? Total;
    }

    public class RenderedFields
    {
        public string Deadline = "-";
        public string PromoCode = "-";
        public string MinimumDeposit = "-";
    }

}
