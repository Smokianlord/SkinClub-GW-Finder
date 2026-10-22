using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SkinClubGiveawayDesktop
{
    // User-editable options, stored next to data.json as settings.json. Every value is
    // clamped on load so a hand-edited file can never put the app into a broken state.
    public class AppSettings
    {
        public int RefreshMinutes { get; set; }
        public int HistoryLimit { get; set; }
        public bool Notifications { get; set; }
        // History shows only each creator's newest ended giveaway (older ones stay saved).
        public bool HistoryLatestOnly { get; set; }
        public string BrowserPath { get; set; }
        public int HttpTimeoutSeconds { get; set; }
        public int RenderTimeoutSeconds { get; set; }
        public int MetadataRetryHours { get; set; }
        public int ProbeConcurrency { get; set; }
        public int PerHostConcurrency { get; set; }
        // Windows-enforced ceilings for the one browser the app runs at a time (root plus children).
        public int BrowserProcessLimit { get; set; }
        public int BrowserConcurrency { get; set; }
        public int BrowserMemoryLimitMB { get; set; }
        // Creator name -> domain, added to the built-in list (for example "Someone": "someone.club").
        public Dictionary<string, string> ExtraCreators { get; set; }

        public AppSettings()
        {
            RefreshMinutes = 10;
            HistoryLimit = 30;
            Notifications = true;
            HistoryLatestOnly = true;
            BrowserPath = "";
            HttpTimeoutSeconds = 20;
            PerHostConcurrency = 4;
            RenderTimeoutSeconds = 30;
            MetadataRetryHours = 12;
            ProbeConcurrency = 64;
            BrowserProcessLimit = 128;
            BrowserConcurrency = 6;
            BrowserMemoryLimitMB = 4096;
            ExtraCreators = new Dictionary<string, string>();
        }

        private static AppSettings current;
        private static readonly object Gate = new object();

        public static string SettingsFile { get { return Path.Combine(DataStore.DataDir, "settings.json"); } }

        public static AppSettings Current
        {
            get { lock (Gate) { if (current == null) current = Load(); return current; } }
            set { lock (Gate) { current = value == null ? new AppSettings() : value; current.Normalize(); } }
        }

        public void Normalize()
        {
            RefreshMinutes = Clamp(RefreshMinutes, 1, 1440);
            HistoryLimit = Clamp(HistoryLimit, 5, 500);
            HttpTimeoutSeconds = Clamp(HttpTimeoutSeconds, 3, 60);
            RenderTimeoutSeconds = Clamp(RenderTimeoutSeconds, 10, 120);
            MetadataRetryHours = Clamp(MetadataRetryHours, 1, 168);
            ProbeConcurrency = Clamp(ProbeConcurrency, 4, 128);
            PerHostConcurrency = Clamp(PerHostConcurrency, 1, 16);
            BrowserProcessLimit = Clamp(BrowserProcessLimit, 5, 256);
            BrowserConcurrency = Clamp(BrowserConcurrency, 1, 16);
            BrowserMemoryLimitMB = Clamp(BrowserMemoryLimitMB, 512, 16384);
            if (BrowserPath == null) BrowserPath = "";
            Dictionary<string, string> clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (ExtraCreators != null)
            {
                foreach (KeyValuePair<string, string> kv in ExtraCreators)
                {
                    string domain = (kv.Value ?? "").Trim().ToLowerInvariant();
                    if (domain.StartsWith("www.")) domain = domain.Substring(4);
                    if (string.IsNullOrWhiteSpace(kv.Key) || !domain.EndsWith(".club") || domain.Contains("/") || domain.Contains(" ")) continue;
                    clean[kv.Key.Trim()] = domain;
                }
            }
            ExtraCreators = clean;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static AppSettings Load()
        {
            AppSettings loaded = null;
            try
            {
                if (File.Exists(SettingsFile))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    loaded = js.Deserialize<AppSettings>(File.ReadAllText(SettingsFile, Encoding.UTF8));
                }
            }
            catch (Exception ex)
            {
                Diag.Swallow("Reading settings.json (defaults used)", ex);
            }
            if (loaded == null) loaded = new AppSettings();
            loaded.Normalize();
            return loaded;
        }

        public void Save()
        {
            try
            {
                Normalize();
                Directory.CreateDirectory(DataStore.DataDir);
                JavaScriptSerializer js = new JavaScriptSerializer();
                string temp = SettingsFile + ".tmp";
                File.WriteAllText(temp, Indent(js.Serialize(this)), new UTF8Encoding(false));
                if (File.Exists(SettingsFile)) File.Replace(temp, SettingsFile, null);
                else File.Move(temp, SettingsFile);
            }
            catch (Exception ex)
            {
                Diag.Swallow("Saving settings.json", ex);
            }
        }

        // Writes the file once so users can find and edit it (ExtraCreators etc.).
        public static void EnsureFileExists()
        {
            if (!File.Exists(SettingsFile)) Current.Save();
        }

        // JavaScriptSerializer emits one line; add line breaks so the file is hand-editable.
        private static string Indent(string json)
        {
            StringBuilder sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inString = !inString;
                if (inString) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) break;
                        depth++; sb.AppendLine().Append(' ', depth * 2);
                        break;
                    case '}': case ']':
                        if (i > 0 && (json[i - 1] == '{' || json[i - 1] == '[')) { sb.Append(c); break; }
                        depth--; sb.AppendLine().Append(' ', depth * 2).Append(c);
                        break;
                    case ',': sb.Append(c).AppendLine().Append(' ', depth * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    // Central place for "this failure is not fatal, but do not hide it" handling and crash reports.
    public static class Diag
    {
        private static readonly object Gate = new object();

        public static void Swallow(string where, Exception ex)
        {
            try { ActivityLog.Write("WARN", where + ": " + (ex == null ? "unknown" : ex.Message)); }
            catch { /* logging must never throw */ }
        }

        public static string CrashFile { get { return Path.Combine(DataStore.DataDir, "crash.log"); } }

        public static void ReportCrash(string source, Exception ex)
        {
            try
            {
                ActivityLog.Write("CRASH", source + ": " + (ex == null ? "unknown" : ex.Message));
                lock (Gate)
                {
                    Directory.CreateDirectory(DataStore.DataDir);
                    FileInfo info = new FileInfo(CrashFile);
                    if (info.Exists && info.Length > 512 * 1024) info.Delete();
                    File.AppendAllText(CrashFile,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + source + Environment.NewLine +
                        (ex == null ? "(no exception object)" : ex.ToString()) + Environment.NewLine + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { /* last resort: nothing else can be done */ }
        }
    }
}
