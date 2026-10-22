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
        private static string HtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = Regex.Replace(html, @"<(?:br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<script\b[^>]*>.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s);
            s = s.Replace("\r", "");
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @"\n\s*\n+", "\n");
            return s.Trim();
        }

        // Participation details must come from the actual "Take part / Step 2"
        // instructions or the Skin.Club promo link.  The page also contains a
        // decorative "use promocode" special-prize CTA; treating the next word
        // after that CTA as a code is what produced bogus values such as SKIN.
        private static readonly Regex PromoQueryRegex = new Regex(
            @"(?:utm_promo|promo_code|promocode)\s*=\s*([A-Za-z0-9][A-Za-z0-9_\-]{2,63})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ParticipationStepRegex = new Regex(
            @"(?:replenish|top[ \t-]*up|deposit|recharge)[\s\S]{0,120}?(?:balance|account)?[\s\S]{0,60}?(?:from|minimum(?:\s+deposit)?(?:\s+of)?|at\s+least)\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])[\s\S]{0,100}?(?:promo(?:tional)?[\s_\-]*code|promocode|c[oó]digo\s*(?:promocional|promo)?)[ \t
:=-]*([A-Za-z0-9][A-Za-z0-9_\-]{2,63})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PromoCodeRegex = new Regex(
            @"(?:promo(?:tional)?[\s_\-]*code|promocode|special[\s_\-]*code|code[\s_\-]*promo|c[oó]digo\s*(?:promocional|promo)?|use\s+(?:promo[\s_\-]*)?code|enter\s+(?:promo[\s_\-]*)?code)\s*(?:(?:for\s+participation|to\s+participate|for\s+the\s+giveaway)\s*)?(?:[:#=\-]|\bis\b)?\s*[""']?([A-Za-z0-9][A-Za-z0-9_\-]{1,63})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MinimumDepositRegex = new Regex(
            @"(?:(?:minimum|min\.?)[\s_\-]*(?:deposit(?:[\s_\-]+amount)?|top[\s_\-]*up(?:[\s_\-]+amount)?|recharge(?:[\s_\-]+amount)?|amount)|deposit[\s_\-]+(?:minimum|min\.?))\s*[:=\-]?\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ParticipationDepositRegex = new Regex(
            @"(?:from|starting\s+from)\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])\s*(?:with|using)\s+(?:a\s+)?promo\s*code",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex JsonMinimumDepositRegex = new Regex(
            @"[""']?(?:minimumDeposit|minimum_deposit|minDeposit|min_deposit|depositMinimum|deposit_minimum|minimumTopup|minimum_topup|minTopup|min_topup|minRecharge|minimumRecharge)[""']?\s*[:=]\s*[""']?\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FlexibleDepositBeforePromoRegex = new Regex(
            @"(?:replenish|top[ \t-]*up|deposit|recharge|add\s+funds|fund\s+(?:your\s+)?balance)[\s\S]{0,180}?(?:from|starting\s+from|at\s+least|minimum(?:\s+of)?|more\s+than|over)?\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])[\s\S]{0,180}?(?:promo(?:tional)?[\s_\-]*code|promocode|c[oó]digo)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FlexiblePromoBeforeDepositRegex = new Regex(
            @"(?:promo(?:tional)?[\s_\-]*code|promocode|c[oó]digo)[\s\S]{0,180}?(?:replenish|top[ \t-]*up|deposit|recharge|add\s+funds|fund\s+(?:your\s+)?balance)[\s\S]{0,120}?(?:from|starting\s+from|at\s+least|minimum(?:\s+of)?|more\s+than|over)?\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Creator templates are not identical. Some pages use wording such as
        // "refill your balance with $5" or translated labels instead of the
        // exact "minimum deposit / from $5" phrasing. These helpers are only
        // used near a verified participation promocode so prize values elsewhere
        // on the giveaway page are not mistaken for the deposit requirement.
        private static readonly Regex DepositContextRegex = new Regex(
            @"replenish|refill|top[ \t-]*up|deposit|recharge|balance|add\s+(?:funds|money)|fund\s+(?:your\s+)?balance|load\s+(?:your\s+)?balance|recarg(?:a|ar|ue)|saldo|dep[oó]sito|depositar|ingres(?:a|ar)|recarregar|carregar\s+saldo|recharg(?:e|er)|solde|einzahl(?:en|ung)|guthaben",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MoneyAmountRegex = new Regex(
            @"(?:(?:US|CA|AU)?[$€£]\s*[0-9]+(?:[.,][0-9]{1,2})?|[0-9]+(?:[.,][0-9]{1,2})?\s*(?:US[$]|CA[$]|AU[$]|[$€£]|USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static int? ParseInt(string s)
        {
            if (s == null) return null;
            string digits = Regex.Replace(s, "[^0-9]", "");
            int v;
            if (int.TryParse(digits, out v)) return v;
            return null;
        }

        private static bool TryParseCountdown(string raw, out TimeSpan span)
        {
            span = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim().ToLowerInvariant();
            double totalSeconds = 0;
            bool found = false;

            MatchCollection units = CountdownUnitRegex.Matches(s);
            foreach (Match m in units)
            {
                double value;
                string number = (m.Groups[1].Value ?? "").Replace(',', '.');
                if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) continue;
                string unit = (m.Groups[2].Value ?? "").ToLowerInvariant();

                if (unit.StartsWith("d") && unit != "dias" && unit != "dia" && unit != "días" && unit != "día")
                    totalSeconds += value * 86400.0;
                else if (unit.StartsWith("day") || unit == "dia" || unit == "dias" || unit == "día" || unit == "días" || unit == "d")
                    totalSeconds += value * 86400.0;
                else if (unit.StartsWith("h") || unit.StartsWith("hora"))
                    totalSeconds += value * 3600.0;
                else if (unit.StartsWith("m") || unit.StartsWith("min"))
                    totalSeconds += value * 60.0;
                else if (unit.StartsWith("s") || unit.StartsWith("seg"))
                    totalSeconds += value;
                found = true;
            }

            // Common countdown forms: HH:MM:SS or DD:HH:MM:SS.
            string withoutUnits = CountdownUnitRegex.Replace(s, " ").Trim();
            Match clock = CountdownClockRegex.Match(withoutUnits);
            if (clock.Success)
            {
                int a = 0, b = 0, c = 0, d = 0;
                if (clock.Groups[1].Success)
                {
                    int.TryParse(clock.Groups[1].Value, out a);
                    int.TryParse(clock.Groups[2].Value, out b);
                    int.TryParse(clock.Groups[3].Value, out c);
                    int.TryParse(clock.Groups[4].Value, out d);
                    totalSeconds += a * 86400.0 + b * 3600.0 + c * 60.0 + d;
                }
                else
                {
                    int.TryParse(clock.Groups[2].Value, out b);
                    int.TryParse(clock.Groups[3].Value, out c);
                    int.TryParse(clock.Groups[4].Value, out d);
                    totalSeconds += b * 3600.0 + c * 60.0 + d;
                }
                found = true;
            }

            if (!found || totalSeconds < 0) return false;
            span = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }

        private static bool TryParseHoursNumber(string raw, out double hours)
        {
            hours = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string number = raw.Trim().Replace(',', '.');
            double parsed;
            if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out parsed)) return false;
            // Sanity limit: a creator giveaway should not run for years. This also
            // prevents Unix timestamps or unrelated large numbers being treated as hours.
            if (parsed < 0 || parsed > 17568) return false;
            hours = parsed;
            return true;
        }

        private static string DeadlineFromHours(double hours)
        {
            DateTime end = DateTime.Now.AddHours(hours);
            return end.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        private static string DeadlineFromRenderedCompletionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";

            string decoded = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
            Match match = VisibleCompletionTimerRegex.Match(decoded);
            if (!match.Success) return "-";

            double hours = 0, minutes = 0, seconds = 0;
            string h = (match.Groups[1].Value ?? "").Replace(',', '.');
            string m = match.Groups[2].Success ? (match.Groups[2].Value ?? "0").Replace(',', '.') : "0";
            string sec = match.Groups[3].Success ? (match.Groups[3].Value ?? "0").Replace(',', '.') : "0";

            if (!double.TryParse(h, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hours)) return "-";
            if (!double.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minutes)) minutes = 0;
            if (!double.TryParse(sec, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds)) seconds = 0;

            if (hours < 0 || hours > 17568 || minutes < 0 || minutes >= 60 || seconds < 0 || seconds >= 60) return "-";

            DateTime end = DateTime.Now.AddSeconds(hours * 3600.0 + minutes * 60.0 + seconds);
            return end.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        private static string DeadlineFromRawHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "-";

            string decoded = WebUtility.HtmlDecode(html);
            Match hoursMatch = RemainingHoursRegex.Match(decoded);
            if (!hoursMatch.Success) hoursMatch = RemainingHoursNearLabelRegex.Match(decoded);
            if (!hoursMatch.Success) hoursMatch = RemainingBareHoursNearLabelRegex.Match(decoded);
            if (hoursMatch.Success)
            {
                double hours;
                if (TryParseHoursNumber(hoursMatch.Groups[1].Value, out hours))
                    return DeadlineFromHours(hours);
            }

            return "-";
        }

        private static string DeadlineFromVisibleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";

            // First handle the normal same-line form.
            MatchCollection matches = DeadlineLineRegex.Matches(text);
            foreach (Match match in matches)
            {
                string raw = Regex.Replace(match.Groups[1].Value ?? "", @"\s+", " ").Trim().Trim(' ', ':', '|');
                if (raw == "" || raw == "-") continue;
                if (ExactEndedValueRegex.IsMatch(raw)) return "Ended";
                string converted = DeadlineAsDate(raw);
                if (converted != "-") return converted;
            }

            // The new layout can render the label and hour count in separate block
            // elements, e.g. "Time to completion:" on one line and "46" on the next.
            Match next = DeadlineNextValueRegex.Match(text);
            if (next.Success)
            {
                string raw = Regex.Replace(next.Groups[1].Value ?? "", @"\s+", " ").Trim();
                if (ExactEndedValueRegex.IsMatch(raw)) return "Ended";
                string converted = DeadlineAsDate(raw);
                if (converted != "-") return converted;
            }

            return "-";
        }

        private static string AbsoluteDeadlineFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "-";

            Match iso = HiddenAbsoluteDeadlineRegex.Match(html);
            if (iso.Success)
            {
                DateTime dt;
                string raw = WebUtility.HtmlDecode(iso.Groups[1].Value ?? "");
                if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal, out dt) ||
                    DateTime.TryParse(raw, out dt))
                {
                    if (dt.Date >= DateTime.Now.Date.AddDays(-1))
                        return dt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
                }
            }

            Match unix = HiddenUnixDeadlineRegex.Match(html);
            if (unix.Success)
            {
                long stamp;
                if (long.TryParse(unix.Groups[1].Value, out stamp))
                {
                    if (stamp > 9999999999L) stamp /= 1000L;
                    try
                    {
                        DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        DateTime dt = epoch.AddSeconds(stamp).ToLocalTime();
                        if (dt.Year >= DateTime.Now.Year - 1 && dt.Year <= DateTime.Now.Year + 5 && dt.Date >= DateTime.Now.Date.AddDays(-1))
                            return dt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
                    }
                    catch { }
                }
            }

            Match seconds = RemainingSecondsRegex.Match(html);
            if (seconds.Success)
            {
                int sec;
                if (int.TryParse(seconds.Groups[1].Value, out sec) && sec > 0)
                {
                    DateTime dt = DateTime.Now.AddSeconds(sec);
                    return dt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
                }
            }

            string hoursDeadline = DeadlineFromRawHtml(html);
            if (hoursDeadline != "-") return hoursDeadline;

            return "-";
        }

        private static string DeadlineAsDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "-";
            string value = Regex.Replace(raw, @"\s+", " ").Trim().Trim(' ', ':', '|');
            if (value == "" || value == "-") return "-";

            double bareHours;
            if (Regex.IsMatch(value.Replace(',', '.'), @"^\d+(?:\.\d+)?$") &&
                TryParseHoursNumber(value, out bareHours))
            {
                return DeadlineFromHours(bareHours);
            }

            TimeSpan countdown;
            if (TryParseCountdown(value, out countdown))
            {
                DateTime end = DateTime.Now.Add(countdown);
                return end.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
            }

            DateTime absolute;
            if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal, out absolute) ||
                DateTime.TryParse(value, out absolute))
            {
                return absolute.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
            }

            // The visible field can be a live timer format we do not recognize yet.
            // Keep the giveaway Active, but do not show the raw timer in the Deadline column.
            return "-";
        }

        private static bool IsClearlyInvalidPromoCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return true;
            string upper = code.Trim().ToUpperInvariant();
            return upper == "GET" || upper == "CODE" || upper == "PROMO" || upper == "PROMOCODE" ||
                   upper == "SPECIAL" || upper == "COPY" || upper == "HERE" || upper == "USE" ||
                   upper == "ENTER" || upper == "FOR" || upper == "WITH" || upper == "USING" ||
                   upper == "THE" || upper == "EVERY" || upper == "SKIN" || upper == "CLUB" ||
                   upper == "BONUS" || upper == "PRIZE" || upper == "POOL" || upper == "DISCOUNT";
        }

        private static string NormalizePromoCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "-";
            string code = WebUtility.UrlDecode(WebUtility.HtmlDecode(raw)).Trim().Trim('"', '\'', ' ', '\t', '\r', '\n');
            if (code.Length < 3 || code.Length > 64 || IsClearlyInvalidPromoCode(code)) return "-";
            if (!Regex.IsMatch(code, @"^[A-Za-z0-9][A-Za-z0-9_\-]{2,63}$")) return "-";
            return code;
        }

        private static string PromoCodeFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";
            string decoded = WebUtility.HtmlDecode(text);

            // Most reliable source: the actual participation instruction:
            // "Replenish your balance from $8 with promo code LP-..."
            // This deliberately comes before generic Skin.Club promo links because
            // the page can also contain a separate "Special Prize" discount code.
            // Raw HTML puts tags between the words and the code ("promo code <br> <b>LP-X</b>"),
            // so also try the text with tags removed.
            string flat = Regex.Replace(decoded, @"<[^>]+>", " ");
            foreach (string source in new string[] { flat, decoded })
            {
                Match step = ParticipationStepRegex.Match(source);
                if (!step.Success) continue;
                string code = NormalizePromoCode(step.Groups[2].Value);
                if (code != "-") return code;
            }

            // Fallback to Skin.Club promo links, but prefer the giveaway
            // participation code (normally LP-...) if multiple promo links exist.
            string firstQueryCode = "-";
            foreach (Match query in PromoQueryRegex.Matches(decoded))
            {
                string code = NormalizePromoCode(query.Groups[1].Value);
                if (code == "-") continue;
                // Skip the separate "discount promocode / special bonus" prize code; only the
                // STEP 2 entry code counts as the giveaway promocode.
                string before = decoded.Substring(Math.Max(0, query.Index - 500), Math.Min(500, query.Index));
                if (Regex.IsMatch(before, @"discount\s+promo|special\s+(?:bonus|prize)", RegexOptions.IgnoreCase)) continue;
                if (code.StartsWith("LP-", StringComparison.OrdinalIgnoreCase)) return code;
                if (firstQueryCode == "-") firstQueryCode = code;
            }
            if (firstQueryCode != "-") return firstQueryCode;

            // Legacy fallback.  Require a structured value; plain UI words such
            // as SKIN must never be accepted as promocodes.
            foreach (Match m in PromoCodeRegex.Matches(decoded))
            {
                string code = NormalizePromoCode(m.Groups[1].Value);
                if (code == "-") continue;
                bool looksStructured = code.StartsWith("LP-", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(code, @"[0-9_\-]");
                if (!looksStructured) continue;
                return code;
            }
            return "-";
        }

        private static string NormalizeDepositAmount(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "-";
            string value = Regex.Replace(WebUtility.HtmlDecode(raw), @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(value)) return "-";
            return value.ToUpperInvariant();
        }

        private static string MinimumDepositFromText(string text)
        {
            return MinimumDepositFromText(text, null);
        }

        private static string MinimumDepositFromText(string text, string knownPromoCode)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";
            string decoded = WebUtility.HtmlDecode(text);

            // Most reliable source: the same Step 2 instruction that contains the
            // participation promocode. This keeps prize-pool dollar values out.
            Match step = ParticipationStepRegex.Match(decoded);
            if (step.Success)
            {
                string amount = NormalizeDepositAmount(step.Groups[1].Value);
                if (amount != "-") return amount;
            }

            Match m = MinimumDepositRegex.Match(decoded);
            if (!m.Success) m = ParticipationDepositRegex.Match(decoded);
            if (!m.Success) m = JsonMinimumDepositRegex.Match(decoded);
            if (!m.Success) m = FlexibleDepositBeforePromoRegex.Match(decoded);
            if (!m.Success) m = FlexiblePromoBeforeDepositRegex.Match(decoded);
            if (m.Success)
            {
                string value = NormalizeDepositAmount(m.Groups[1].Value);
                if (value != "-") return value;
            }

            // Some creator templates use different wording but still render the
            // promocode and minimum deposit beside one another. Restrict this
            // fallback to a window around the already-verified participation code.
            if (!string.IsNullOrWhiteSpace(knownPromoCode) && knownPromoCode != "-")
            {
                int promoIndex = decoded.IndexOf(knownPromoCode, StringComparison.OrdinalIgnoreCase);
                if (promoIndex >= 0)
                {
                    int start = Math.Max(0, promoIndex - 700);
                    int length = Math.Min(decoded.Length - start, 1400);
                    string nearby = decoded.Substring(start, length);

                    // Keep the older explicit phrasing first.
                    Match amountNearPromo = Regex.Match(nearby,
                        @"(?:from|starting\s+from|at\s+least|minimum(?:\s+deposit)?(?:\s+of)?|more\s+than|over|for|by)\s*((?:US[$]|CA[$]|AU[$]|[$€£])?\s*[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT|[$€£])?)",
                        RegexOptions.IgnoreCase);
                    if (amountNearPromo.Success && DepositContextRegex.IsMatch(nearby))
                    {
                        string value = NormalizeDepositAmount(amountNearPromo.Groups[1].Value);
                        if (value != "-") return value;
                    }

                    // Final safe fallback: choose the monetary amount whose local
                    // context looks like a deposit/balance instruction and that is
                    // closest to the verified promo code. This covers templates such
                    // as "refill balance $5", "deposit 10$", and translated labels.
                    int promoLocal = promoIndex - start;
                    Match bestMoney = null;
                    int bestDistance = int.MaxValue;
                    foreach (Match money in MoneyAmountRegex.Matches(nearby))
                    {
                        int ctxStart = Math.Max(0, money.Index - 180);
                        int ctxLength = Math.Min(nearby.Length - ctxStart, money.Length + 360);
                        string context = nearby.Substring(ctxStart, ctxLength);
                        if (!DepositContextRegex.IsMatch(context)) continue;

                        int distance = Math.Abs((money.Index + money.Length / 2) - promoLocal);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestMoney = money;
                        }
                    }
                    if (bestMoney != null)
                    {
                        string value = NormalizeDepositAmount(bestMoney.Value);
                        if (value != "-") return value;
                    }
                }
            }
            return "-";
        }

        public static ParseResult ParsePage(string html)
        {
            string text = HtmlToText(html);

            int? remaining = null;
            int? total = null;
            Match tm = TicketLineRegex.Match(text);
            if (!tm.Success) tm = TicketFallbackRegex.Match(text);
            if (tm.Success)
            {
                remaining = ParseInt(tm.Groups[1].Value);
                total = ParseInt(tm.Groups[2].Value);
            }
            string ticket = (remaining.HasValue && total.HasValue)
                ? string.Format("{0} / {1}", remaining.Value, total.Value)
                : "-";
            string decodedHtml = WebUtility.HtmlDecode(html);
            string promoCode = PromoCodeFromText(decodedHtml);
            if (promoCode == "-") promoCode = PromoCodeFromText(text);
            string minimumDeposit = MinimumDepositFromText(text, promoCode);
            if (minimumDeposit == "-") minimumDeposit = MinimumDepositFromText(decodedHtml, promoCode);

            string deadline = "-";
            bool explicitEnded = false;
            bool hasLiveDeadlineSignal = false;

            // Prefer the exact current visible timer format before any legacy parser.
            string visibleDeadline = DeadlineFromRenderedCompletionText(text);
            if (visibleDeadline == "-") visibleDeadline = DeadlineFromVisibleText(text);
            if (string.Equals(visibleDeadline, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                explicitEnded = true;
                deadline = "Ended";
            }
            else if (visibleDeadline != "-")
            {
                hasLiveDeadlineSignal = true;
                deadline = visibleDeadline;
            }
            else
            {
                // On the current SkinClub layout the visible completion value is injected
                // by JavaScript. If the static page already contains the timer label, do
                // NOT trust hidden endTime/deadline/remainingTime values: some of those
                // belong to unrelated page state and were the source of the wrong dates.
                bool currentCompletionLayout = Regex.IsMatch(text, @"time\s*to\s*completion", RegexOptions.IgnoreCase);
                if (currentCompletionLayout)
                {
                    hasLiveDeadlineSignal = true;
                }
                else
                {
                    // Legacy creator pages may still expose a real absolute deadline.
                    deadline = AbsoluteDeadlineFromHtml(html);
                    if (deadline != "-") hasLiveDeadlineSignal = true;
                    if (!hasLiveDeadlineSignal && DeadlineLineRegex.IsMatch(text))
                        hasLiveDeadlineSignal = true;
                }
            }

            bool soldOut = remaining.HasValue && remaining.Value <= 0;
            bool hasTickets = remaining.HasValue && remaining.Value > 0;

            string status;
            if (explicitEnded || soldOut) status = "ended";
            else if (hasTickets || hasLiveDeadlineSignal) status = "active";
            else status = "unknown";

            return new ParseResult
            {
                Status = status,
                Ticket = ticket,
                PromoCode = promoCode,
                MinimumDeposit = minimumDeposit,
                Deadline = explicitEnded ? "Ended" : deadline,
                Remaining = remaining,
                Total = total
            };
        }
    }
}
