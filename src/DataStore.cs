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
    public static class DataStore
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkinClub GW Finder");
        public static readonly string DataFile = Path.Combine(DataDir, "data.json");
        private static readonly object FileLock = new object();

        public static AppData Load()
        {
            Directory.CreateDirectory(DataDir);
            ImportOldDataIfPresent();
            if (!File.Exists(DataFile))
            {
                AppData initial = DefaultData();
                Save(initial);
                return initial;
            }
            try
            {
                string json = File.ReadAllText(DataFile, Encoding.UTF8);
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                AppData data = js.Deserialize<AppData>(json);
                if (data == null) data = DefaultData();
                if (data.Items == null) data.Items = new List<GiveawayItem>();
                data.Items.RemoveAll(delegate(GiveawayItem item) { return item == null || string.IsNullOrWhiteSpace(item.Url); });
                foreach (GiveawayItem item in data.Items)
                {
                    if (item == null) continue;
                    string promo = (item.PromoCode ?? "").Trim();
                    string upper = promo.ToUpperInvariant();
                    if (upper == "SKIN" || upper == "CLUB" || upper == "BONUS" ||
                        upper == "PRIZE" || upper == "POOL" || upper == "DISCOUNT" ||
                        upper == "PROMO" || upper == "PROMOCODE" || upper == "CODE")
                        item.PromoCode = "-";
                }

                // Repair metadata contaminated by the old concurrent Chromium/CDP race, and entry
                // codes overwritten by an ended page's unrelated code (see GiveawayInfo). Keep a
                // one-time copy of the file before the first repair so nothing is lost.
                bool repaired = GiveawayInfo.RepairEntryCodes(data);
                string beforeRepair = DataFile + ".before-code-repair";
                if (repaired && !File.Exists(beforeRepair)) File.Copy(DataFile, beforeRepair, false);
                int previousCount = data.Items.Count;
                PruneOldHistory(data);
                if (repaired || data.Items.Count != previousCount) Save(data);
                return data;
            }
            catch
            {
                // Preserve unreadable data for recovery instead of silently destroying it.
                File.Copy(DataFile, DataFile + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
                AppData fallback = DefaultData();
                Save(fallback);
                return fallback;
            }
        }

        public static void Save(AppData data)
        {
            PruneOldHistory(data);
            lock (FileLock)
            {
                Directory.CreateDirectory(DataDir);
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                string json = js.Serialize(data);
                string temp = DataFile + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                if (File.Exists(DataFile)) File.Replace(temp, DataFile, DataFile + ".bak");
                else File.Move(temp, DataFile);
            }
        }

        private static void PruneOldHistory(AppData data)
        {
            if (data == null || data.Items == null) return;

            // History is capped by recency, independently of the current grid sort. Joined and
            // active entries never count toward the limit, and each creator's newest ended
            // giveaway is always kept so the History tab can show it.
            List<GiveawayItem> ended = data.Items.Where(delegate(GiveawayItem item) {
                return item != null && !item.Joined &&
                    string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase);
            }).ToList();
            HashSet<GiveawayItem> keep = new HashSet<GiveawayItem>(GiveawayInfo.LatestPerCreator(ended));
            int slots = Math.Max(0, AppSettings.Current.HistoryLimit - keep.Count);
            foreach (GiveawayItem item in ended
                .Where(delegate(GiveawayItem x) { return !keep.Contains(x); })
                .OrderByDescending(GiveawayInfo.Recency)
                .ThenBy(delegate(GiveawayItem x) { return x.Url ?? ""; }, StringComparer.OrdinalIgnoreCase)
                .Take(slots))
                keep.Add(item);
            data.Items.RemoveAll(delegate(GiveawayItem item) { return ended.Contains(item) && !keep.Contains(item); });
        }
        private static void ImportOldDataIfPresent()
        {
            if (File.Exists(DataFile)) return;
            try
            {
                // Migrate data from older builds after the app was renamed.
                string legacyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SkinClub Giveaway Dashboard");
                string legacyFile = Path.Combine(legacyDir, "data.json");
                if (File.Exists(legacyFile))
                {
                    File.Copy(legacyFile, DataFile, false);
                    return;
                }

                string old = Path.Combine(Application.StartupPath, "data.json");
                if (File.Exists(old)) File.Copy(old, DataFile, false);
            }
            catch (Exception swallowed) { Diag.Swallow("Importing data from an older version", swallowed); }
        }

        private static AppData DefaultData()
        {
            AppData d = new AppData();
            AddSeed(d, "Jon Sandman", "https://jonsandman.club/010926/");
            AddSeed(d, "McNasty", "https://mcnasty.club/010826/");
            AddSeed(d, "Blarg", "https://blarg.club/010826/");
            AddSeed(d, "ErycTriceps", "https://eryctriceps.club/010826/");
            AddSeed(d, "Joaco", "https://joaco.club/010826/");
            return d;
        }

        private static void AddSeed(AppData d, string creator, string url)
        {
            d.Items.Add(new GiveawayItem
            {
                Creator = creator,
                Url = url,
                Status = "unknown",
                Ticket = "-",
                PromoCode = "-",
                MinimumDeposit = "-",
                Deadline = "-",
                Source = "seed"
            });
        }
    }
}
