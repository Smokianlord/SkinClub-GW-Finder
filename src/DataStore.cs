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

                    // Repair metadata contaminated by the old concurrent Chromium/CDP
                    // race. Participation codes normally carry the same DDMMYY slug as
                    // the giveaway URL. If the saved code points at a different dated
                    // giveaway (for example SAMZ /010926 with ...-010826), discard the
                    // promo/deposit so the normal refresh can repopulate them correctly.
                    if (!string.IsNullOrWhiteSpace(item.PromoCode) && item.PromoCode != "-" &&
                        !string.IsNullOrWhiteSpace(item.Url))
                    {
                        Match promoDate = Regex.Match(item.PromoCode, @"-(\d{6})$", RegexOptions.IgnoreCase);
                        Match urlDate = Regex.Match(item.Url, @"/(\d{6})(?:/|$|\?)", RegexOptions.IgnoreCase);
                        if (promoDate.Success && urlDate.Success &&
                            !string.Equals(promoDate.Groups[1].Value, urlDate.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                        {
                            item.PromoCode = "-";
                            item.MinimumDeposit = "-";
                        }
                    }
                }
                int previousCount = data.Items.Count;
                PruneOldHistory(data);
                if (data.Items.Count != previousCount) Save(data);
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

            // History is capped by recency, independently of the current grid sort.
            // Joined and active entries never count toward this limit.
            HashSet<GiveawayItem> older = new HashSet<GiveawayItem>(data.Items
                .Where(delegate(GiveawayItem item) {
                    return item != null && !item.Joined &&
                        string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(HistoryRecency)
                .ThenBy(delegate(GiveawayItem item) { return item.Url ?? ""; }, StringComparer.OrdinalIgnoreCase)
                .Skip(30));
            data.Items.RemoveAll(delegate(GiveawayItem item) { return older.Contains(item); });
        }

        private static DateTime HistoryRecency(GiveawayItem item)
        {
            DateTime timestamp;
            if (DateTime.TryParse(item.HistorySince, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out timestamp))
                return timestamp.ToUniversalTime();
            if (DateTime.TryParse(item.LastChecked, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out timestamp))
                return timestamp.ToUniversalTime();
            return DateTime.MinValue;
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
            catch { }
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
