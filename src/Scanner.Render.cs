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
    public static partial class Scanner
    {
        private static string FindChromiumBrowser()
        {
            string custom = AppSettings.Current.BrowserPath;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
            if (!string.IsNullOrWhiteSpace(custom)) ActivityLog.Write("WARN", "Configured browser not found, using automatic detection: " + custom);

            List<string> paths = new List<string>();
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(pf86))
            {
                paths.Add(Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"));
                paths.Add(Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"));
            }
            if (!string.IsNullOrWhiteSpace(pf))
            {
                paths.Add(Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"));
                paths.Add(Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"));
                paths.Add(Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            }
            if (!string.IsNullOrWhiteSpace(local))
            {
                paths.Add(Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"));
                paths.Add(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
                paths.Add(Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            }

            foreach (string path in paths)
                if (File.Exists(path)) return path;
            return null;
        }

        private static int GetFreeTcpPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally { listener.Stop(); }
        }

        private static string JsonStringValue(object value)
        {
            return value == null ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static async Task<string> DevToolsEvaluateAsync(string websocketUrl, string expression, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(websocketUrl)) return "";
            using (ClientWebSocket ws = new ClientWebSocket())
            {
                ws.Options.Proxy = null;
                await ws.ConnectAsync(new Uri(websocketUrl), token);

                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["id"] = 1;
                payload["method"] = "Runtime.evaluate";
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                parameters["expression"] = expression;
                parameters["returnByValue"] = true;
                parameters["awaitPromise"] = true;
                payload["params"] = parameters;

                byte[] outgoing = Encoding.UTF8.GetBytes(js.Serialize(payload));
                await ws.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Text, true, token);

                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    DateTime expires = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < expires && ws.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult rr;
                        rr = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                        if (rr.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, rr.Count);
                        if (!rr.EndOfMessage) continue;

                        string message = Encoding.UTF8.GetString(ms.ToArray());
                        ms.SetLength(0);
                        try
                        {
                            Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(message);
                            object idObj;
                            if (!root.TryGetValue("id", out idObj) || Convert.ToInt32(idObj) != 1) continue;
                            object resultObj;
                            if (!root.TryGetValue("result", out resultObj)) return "";
                            Dictionary<string, object> result = resultObj as Dictionary<string, object>;
                            if (result == null) return "";
                            object innerObj;
                            if (!result.TryGetValue("result", out innerObj)) return "";
                            Dictionary<string, object> inner = innerObj as Dictionary<string, object>;
                            if (inner == null) return "";
                            object valueObj;
                            if (!inner.TryGetValue("value", out valueObj)) return "";
                            return JsonStringValue(valueObj);
                        }
                        catch (Exception swallowed) { Diag.Swallow("Reading DevTools result", swallowed); return ""; }
                    }
                }
            }
            return "";
        }

        private static RenderedFields CloneRenderedFields(RenderedFields source)
        {
            RenderedFields copy = new RenderedFields();
            if (source == null) return copy;
            copy.Deadline = source.Deadline;
            copy.PromoCode = source.PromoCode;
            copy.MinimumDeposit = source.MinimumDeposit;
            return copy;
        }

        private static bool TryGetRenderedCache(string url, out RenderedFields fields)
        {
            fields = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            lock (RenderCacheGate)
            {
                Tuple<DateTime, RenderedFields> cached;
                if (!RenderCache.TryGetValue(url, out cached)) return false;
                if (DateTime.UtcNow - cached.Item1 > RenderCacheLifetime)
                {
                    RenderCache.Remove(url);
                    return false;
                }
                fields = CloneRenderedFields(cached.Item2);
                return true;
            }
        }

        private static void StoreRenderedCache(string url, RenderedFields fields)
        {
            if (string.IsNullOrWhiteSpace(url) || fields == null) return;
            lock (RenderCacheGate)
            {
                RenderCache[url] = Tuple.Create(DateTime.UtcNow, CloneRenderedFields(fields));
            }
        }

        private static void KillProcessTree(Process process)
        {
            BrowserProcessManager.KillProcessTree(process);
        }

        private static void RemoveRenderProfile(string profileDir)
        {
            if (string.IsNullOrWhiteSpace(profileDir)) return;
            string fullPath = Path.GetFullPath(profileDir);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullPath).StartsWith("SkinClubGWFinder_", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a profile outside the app's temporary folder");
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                    return;
                }
                catch (IOException ex)
                {
                    if (attempt == 7) { ActivityLog.Write("CLEANUP", "Temporary profile still locked after browser exit: " + ex.Message); return; }
                    Thread.Sleep(150);
                }
                catch (UnauthorizedAccessException ex)
                { ActivityLog.Write("CLEANUP", "Temporary profile could not be removed: " + ex.Message); return; }
            }
        }

        private static async Task<RenderedFields> FetchRenderedFieldsAsync(string url)
        {
            RenderedFields cachedFields;
            if (TryGetRenderedCache(url, out cachedFields))
            { ActivityLog.Write("CACHE", "Using rendered metadata: " + url); return cachedFields; }

            RenderedFields best = new RenderedFields();
            if (string.IsNullOrWhiteSpace(url)) return best;
            string browser = FindChromiumBrowser();
            if (string.IsNullOrWhiteSpace(browser))
            { ActivityLog.Write("ERROR", "No Edge or Chrome installation found for " + url); return best; }

            Interlocked.Increment(ref queuedRenders);
            ActivityLog.Write("QUEUE", "Waiting to render " + url);
            try { await RenderFallbackThrottle.WaitAsync(BrowserProcessManager.ShutdownToken); }
            finally { Interlocked.Decrement(ref queuedRenders); }
            string profileDir = null;
            Process process = null;
            HttpClient localClient = null;
            CancellationTokenSource renderCts = CancellationTokenSource.CreateLinkedTokenSource(BrowserProcessManager.ShutdownToken);
            renderCts.CancelAfter(TimeSpan.FromSeconds(AppSettings.Current.RenderTimeoutSeconds));
            CancellationToken token = renderCts.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                if (TryGetRenderedCache(url, out cachedFields)) return cachedFields;
                ActivityLog.Write("RENDER", "Loading " + url);
                // Let Chromium choose its own DevTools port and use an isolated profile.
                profileDir = Path.Combine(Path.GetTempPath(), "SkinClubGWFinder_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(profileDir);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = browser;
                psi.Arguments = "--headless=new --disable-gpu --in-process-gpu --enable-features=NetworkServiceInProcess2 --disable-extensions --no-first-run --no-default-browser-check " +
                                "--disable-sync --mute-audio --disable-background-networking --disable-component-update " +
                                "--disable-features=MediaRouter,OptimizationHints,Translate,BackForwardCache " +
                                "--renderer-process-limit=1 --disable-background-mode --disable-crash-reporter --blink-settings=imagesEnabled=false " +
                                "--remote-allow-origins=* --remote-debugging-port=0" +
                                " --user-data-dir=\"" + profileDir + "\" \"" + url.Replace("\"", "") + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                process = BrowserProcessManager.StartHidden(psi.FileName, psi.Arguments);
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception swallowed) { Diag.Swallow("Lowering browser priority", swallowed); }

                HttpClientHandler localHandler = new HttpClientHandler();
                localHandler.UseProxy = false;
                localClient = new HttpClient(localHandler);
                localClient.Timeout = TimeSpan.FromSeconds(2);
                string websocketUrl = "";
                int port = 0;
                string requestedHost = "";
                try { requestedHost = new Uri(url).Host; } catch (Exception swallowed) { Diag.Swallow("Reading requested host", swallowed); }
                DateTime discoveryDeadline = DateTime.UtcNow.AddSeconds(8);

                // With --remote-debugging-port=0 Chromium writes the actual selected port
                // to DevToolsActivePort in this render's unique profile directory. This is
                // independent of other applications using DevTools.
                while (DateTime.UtcNow < discoveryDeadline && port <= 0)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string activePortFile = Path.Combine(profileDir, "DevToolsActivePort");
                        if (File.Exists(activePortFile))
                        {
                            string[] lines = File.ReadAllLines(activePortFile);
                            int parsedPort;
                            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out parsedPort) && parsedPort > 0)
                                port = parsedPort;
                        }
                    }
                    catch (IOException) { /* DevToolsActivePort is still being written; retry */ }
                    if (port <= 0) await Task.Delay(100, token);
                }

                while (DateTime.UtcNow < discoveryDeadline && port > 0 && string.IsNullOrWhiteSpace(websocketUrl))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string tabsJson;
                        using (HttpResponseMessage response = await localClient.GetAsync("http://127.0.0.1:" + port + "/json/list", token))
                        {
                            response.EnsureSuccessStatusCode();
                            tabsJson = await response.Content.ReadAsStringAsync();
                        }
                        JavaScriptSerializer serializer = new JavaScriptSerializer();
                        object[] tabs = serializer.Deserialize<object[]>(tabsJson);
                        foreach (object tabObj in tabs)
                        {
                            Dictionary<string, object> tab = tabObj as Dictionary<string, object>;
                            if (tab == null) continue;
                            string type = tab.ContainsKey("type") ? JsonStringValue(tab["type"]) : "";
                            string tabUrl = tab.ContainsKey("url") ? JsonStringValue(tab["url"]) : "";
                            string ws = tab.ContainsKey("webSocketDebuggerUrl") ? JsonStringValue(tab["webSocketDebuggerUrl"]) : "";
                            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ws))
                                continue;

                            // Never fall back to the first page target. Only attach to the
                            // target whose host matches the giveaway currently being validated.
                            // This is a second guard against cross-page metadata contamination.
                            Uri tabUri;
                            if (!string.IsNullOrWhiteSpace(requestedHost) &&
                                Uri.TryCreate(tabUrl, UriKind.Absolute, out tabUri) &&
                                string.Equals(tabUri.Host, requestedHost, StringComparison.OrdinalIgnoreCase))
                            {
                                websocketUrl = ws;
                                break;
                            }
                        }
                    }
                    catch (HttpRequestException) { /* DevTools endpoint is not up yet; retry */ }
                    catch (TaskCanceledException) { if (token.IsCancellationRequested) throw; }
                    if (string.IsNullOrWhiteSpace(websocketUrl)) await Task.Delay(150, token);
                }

                if (string.IsNullOrWhiteSpace(websocketUrl))
                { ActivityLog.Write("TIMEOUT", "No page target within 8 seconds: " + url); return best; }

                // Read the text the user actually sees. The previous build scanned
                // hidden JS/state too, which could pick an unrelated timer and produce
                // SEP 11/SEP 12 even when the page visibly showed 161H 59M 2S.
                string expression = @"(() => {
                    try {
                        if (!document.body) return '';
                        return document.body.innerText || document.body.textContent || '';
                    } catch (e) {
                        return '';
                    }
                })()";

                // Promo/deposit values are sometimes present in hidden fields or data-*
                // attributes even when the visible page only shows a generic FAQ. Collect
                // only relevant DOM metadata so we can find those values without using
                // hidden timer state for the deadline.
                string metadataExpression = @"(() => {
                    try {
                        const out = [];
                        if (document.documentElement) out.push(document.documentElement.textContent || '');
                        document.querySelectorAll('*').forEach(el => {
                            try {
                                if (typeof el.value === 'string' && el.value) out.push('value=' + el.value);
                                if (el.href && /(?:utm_promo|promo_code|promocode)=/i.test(el.href)) out.push('href=' + el.href);
                                if (!el.attributes) return;
                                for (const a of el.attributes) {
                                    const pair = (a.name || '') + '=' + (a.value || '');
                                    if (/promo|code|deposit|minimum|min[-_ ]?deposit|top[-_ ]?up|recharge/i.test(pair)) out.push(pair);
                                }
                            } catch (_) {}
                        });
                        // Creator templates sometimes keep participation settings only
                        // in JSON/application state. Read promo/deposit-related script
                        // and storage text, but deliberately ignore timer/deadline state.
                        document.querySelectorAll('script').forEach(s => {
                            try {
                                const t = s.textContent || '';
                                if (/promo|promocode|minimumDeposit|minDeposit|minimum_deposit|min_deposit|deposit|topup|top_up|recharge/i.test(t))
                                    out.push(t.slice(0, 60000));
                            } catch (_) {}
                        });
                        try {
                            for (let i = 0; i < localStorage.length; i++) {
                                const k = localStorage.key(i) || '';
                                const v = localStorage.getItem(k) || '';
                                if (/promo|deposit|minimum|minDeposit|topup|recharge/i.test(k + ' ' + v)) out.push(k + '=' + v);
                            }
                        } catch (_) {}
                        try {
                            for (let i = 0; i < sessionStorage.length; i++) {
                                const k = sessionStorage.key(i) || '';
                                const v = sessionStorage.getItem(k) || '';
                                if (/promo|deposit|minimum|minDeposit|topup|recharge/i.test(k + ' ' + v)) out.push(k + '=' + v);
                            }
                        } catch (_) {}
                        return out.join('\n').slice(0, 300000);
                    } catch (e) {
                        return '';
                    }
                })()";

                // The countdown is populated asynchronously. Poll the live DOM instead of
                // taking one snapshot too early. Keep polling after the deadline appears so
                // promocode/minimum-deposit fields that render slightly later are not missed.
                DateTime renderDeadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < renderDeadline)
                {
                    string renderedData = await DevToolsEvaluateAsync(websocketUrl, expression, token);
                    string metadata = await DevToolsEvaluateAsync(websocketUrl, metadataExpression, token);

                    if (!string.IsNullOrWhiteSpace(renderedData) || !string.IsNullOrWhiteSpace(metadata))
                    {
                        string promo = PromoCodeFromText(renderedData);
                        if (promo == "-") promo = PromoCodeFromText(metadata);
                        if (promo != "-") best.PromoCode = promo;

                        string promoForDeposit = promo != "-" ? promo : best.PromoCode;
                        string minDeposit = MinimumDepositFromText(renderedData, promoForDeposit);
                        if (minDeposit == "-") minDeposit = MinimumDepositFromText(metadata, promoForDeposit);
                        if (minDeposit != "-") best.MinimumDeposit = minDeposit;

                        // The visible TIME TO COMPLETION timer is authoritative. Never use
                        // hidden DOM state for the deadline because it previously caused
                        // unrelated timestamps to be displayed as giveaway deadlines.
                        string deadline = DeadlineFromRenderedCompletionText(renderedData);
                        if (deadline == "-") deadline = DeadlineFromVisibleText(renderedData);
                        if (deadline != "-") best.Deadline = deadline;

                        if (best.Deadline != "-" && best.PromoCode != "-" && best.MinimumDeposit != "-")
                        {
                            StoreRenderedCache(url, best);
                            ActivityLog.Write("RENDER", "Metadata complete: " + url);
                            return CloneRenderedFields(best);
                        }
                    }
                    await Task.Delay(500, token);
                }
                StoreRenderedCache(url, best);
                ActivityLog.Write("RENDER", "Finished with partial metadata: " + url);
                return CloneRenderedFields(best);
            }
            catch (Exception ex)
            {
                ActivityLog.Write(ex is OperationCanceledException ? "TIMEOUT" : "ERROR", "Render stopped: " + url + " — " + ex.Message);
                return CloneRenderedFields(best);
            }
            finally
            {
                try
                {
                    KillProcessTree(process);
                    RemoveRenderProfile(profileDir);
                }
                catch (Exception ex) { ActivityLog.Write("ERROR", "Browser cleanup failed; further launches blocked: " + ex.Message); }
                finally
                {
                    if (localClient != null) localClient.Dispose();
                    renderCts.Dispose();
                    RenderFallbackThrottle.Release();
                }
            }
        }

    }
}
