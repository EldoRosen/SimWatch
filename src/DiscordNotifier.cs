using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SimWatch
{
    /// <summary>
    /// Posts reports to a Discord webhook as embeds: a header block, an estimated-cause block, one code block per command,
    /// and a closing block with the footer. Long reports are split over several messages.
    /// </summary>
    public sealed class DiscordNotifier
    {
        public const string FooterText = "Created by Eldo Rosen for The Destiny Universe";

        const int MaxCodeChars = 3800;       // per code block; an embed description may hold 4096
        const int MaxMessageChars = 5500;    // Discord allows 6000 characters of embed text per message
        const int MaxEmbedsPerMessage = 10;
        const int MaxMessages = 12;
        const int Cyan = 0x00E5FF;
        const int Orange = 0xE67E22;
        const string GalaxyImageName = "galaxy-offender.png";

        // the Galaxy Offenders logo, embedded in the plugin dll
        static readonly byte[] GalaxyImage = LoadResource(GalaxyImageName);

        static byte[] LoadResource(string name)
        {
            try
            {
                using (var stream = typeof(DiscordNotifier).Assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        return memory.ToArray();
                    }
                }
            }
            catch
            {
                return null; // the bulletin is posted without the logo
            }
        }

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        static readonly Regex RetryAfterRegex = new Regex("\"retry_after\"\\s*:\\s*([0-9.]+)", RegexOptions.Compiled);

        sealed class Embed
        {
            public string Title;
            public string Description;
            public int Color;
            public string Footer;
            public string Timestamp;
            public string Thumbnail;   // url, or attachment://name for a file sent with the message
            public int Length => (Title?.Length ?? 0) + (Description?.Length ?? 0) + (Footer?.Length ?? 0);
        }

        readonly Func<SimWatchConfig> _getConfig;

        static DiscordNotifier()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public DiscordNotifier(Func<SimWatchConfig> getConfig)
        {
            _getConfig = getConfig;
        }

        /// <summary>A webhook URL is set (used by `!simwatch scan`, which always sends).</summary>
        public bool HasWebhook => !string.IsNullOrWhiteSpace(_getConfig().DiscordWebhookUrl);

        /// <summary>Automatic sending is on and a URL is set (used by the periodic check).</summary>
        public bool IsEnabled => _getConfig().DiscordEnabled && HasWebhook;

        /// <returns>null on success, otherwise an error description.</returns>
        public async Task<string> SendReportAsync(DiscordReport report)
        {
            try
            {
                var config = _getConfig();
                var urlError = ValidateUrl(config, out var url);
                if (urlError != null) return urlError;

                var messages = BuildMessages(report);

                for (var i = 0; i < messages.Count; i++)
                {
                    var isLast = i == messages.Count - 1;
                    var attach = isLast && config.DiscordAttachFile;

                    // ping the role on the first message of a sim speed alert only
                    var ping = i == 0 && report.Kind == "SIM SPEED ALERT" ? PingText(config, "sim speed alert") : null;

                    var error = await PostAsync(url, BuildPayload(config, messages[i], ping),
                        attach ? report.FileName : null, attach ? report.FullText : null).ConfigureAwait(false);
                    if (error != null) return $"message {i + 1}/{messages.Count}: {error}";

                    if (!isLast) await Task.Delay(700).ConfigureAwait(false); // stay under the webhook rate limit
                }

                return null;
            }
            catch (Exception e)
            {
                return $"Request failed: {e.Message}";
            }
        }

        /// <returns>A human-readable result for the GUI.</returns>
        public async Task<string> SendTestAsync(string profilerLine = null)
        {
            try
            {
                var config = _getConfig();
                var urlError = ValidateUrl(config, out var url);
                if (urlError != null) return urlError;

                var embed = new Embed
                {
                    Title = "\u258CSIMWATCH // WEBHOOK TEST",
                    Description = "```yaml\nstatus   : online\nresult   : webhook reachable\n" +
                                  (string.IsNullOrEmpty(config.SectorName) ? "" : "sector   : " + Clean(config.SectorName) + "\n") +
                                  (string.IsNullOrEmpty(profilerLine) ? "" : "profiler : " + Clean(profilerLine) + "\n") + "```",
                    Color = Cyan,
                    Footer = FooterText,
                    Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };

                var error = await PostAsync(url, BuildPayload(config, new List<Embed> { embed }, PingText(config, "webhook test")), null, null).ConfigureAwait(false);
                return error ?? "Test message sent.";
            }
            catch (Exception e)
            {
                return $"Request failed: {e.Message}";
            }
        }

        static string ValidateUrl(SimWatchConfig config, out string url)
        {
            url = config.DiscordWebhookUrl?.Trim();
            if (string.IsNullOrEmpty(url)) return "Webhook URL is empty.";
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "Webhook URL must start with https://";
            return null;
        }

        // ---- Galaxy Offenders ----

        const string PilotBadgeName = "pilot-badge.png";
        const int MaxCaseFiles = 5;    // structures / scripts listed in the main record
        const int MaxPilotFiles = 4;   // pilots get an embed of their own, with the badge as icon
        const int PilotColor = 0xD35230;

        // the pilot badge shown on player entries
        static readonly byte[] PilotBadge = LoadResource(PilotBadgeName);

        static readonly TimeZoneInfo UkZone = FindUkZone();

        static TimeZoneInfo FindUkZone()
        {
            foreach (var id in new[] { "GMT Standard Time", "Europe/London" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (Exception)
                {
                    // try the next name
                }
            }

            return null; // falls back to UTC
        }

        static DateTime UkLocal(DateTime utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return UkZone == null ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, UkZone);
        }

        /// <summary>"10 Oct 14:32 BST" (UK time), or UTC if the UK time zone isn't available.</summary>
        static string Uk(DateTime utc)
        {
            var local = UkLocal(utc);
            var tag = UkZone == null ? "UTC" : UkZone.IsDaylightSavingTime(local) ? "BST" : "GMT";
            return local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + " " + tag;
        }

        /// <summary>Galaxy Offenders is switched on and has a webhook URL.</summary>
        public bool GalaxyReady
        {
            get
            {
                var config = _getConfig();
                return config.GalaxyEnabled && !string.IsNullOrWhiteSpace(config.GalaxyWebhookUrl);
            }
        }

        /// <returns>null on success, otherwise an error description.</returns>
        public async Task<string> SendGalaxyAsync(IList<OffenderHistory.Offender> offenders, GalaxyContext context, bool isTest = false)
        {
            try
            {
                var config = _getConfig();
                var url = config.GalaxyWebhookUrl?.Trim();
                if (string.IsNullOrEmpty(url)) return "Galaxy Offenders webhook URL is empty.";
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "Galaxy Offenders webhook URL must start with https://";

                var embeds = BuildGalaxyEmbeds(config, offenders, context, isTest);

                // the pictures travel with the message and the embeds point at them
                var images = new List<KeyValuePair<string, byte[]>>();
                if (GalaxyImage != null) images.Add(new KeyValuePair<string, byte[]>(GalaxyImageName, GalaxyImage));
                if (PilotBadge != null && embeds.Any(e => e.Thumbnail == "attachment://" + PilotBadgeName))
                {
                    images.Add(new KeyValuePair<string, byte[]>(PilotBadgeName, PilotBadge));
                }

                return await PostAsync(url, BuildPayload(config, embeds, null, config.GalaxyUsername), null, null, images).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return $"Request failed: {e.Message}";
            }
        }

        /// <returns>A human-readable result for the GUI.</returns>
        public async Task<string> SendGalaxyTestAsync()
        {
            var now = DateTime.UtcNow;
            var sample = new List<OffenderHistory.Offender>
            {
                new OffenderHistory.Offender { Category = "grids", Display = "Sample Station Alpha", Count = 10, FirstUtc = now.AddDays(-5), LastUtc = now.AddHours(-2), Gps = "GPS:Sample Station Alpha:1000.00:2000.00:3000.00:#FF0000:" },
                new OffenderHistory.Offender { Category = "scripts", Display = "Sample Sorting Script", Count = 3, FirstUtc = now.AddDays(-2), LastUtc = now.AddHours(-5) },
                new OffenderHistory.Offender { Category = "players", Display = "Sample Pilot", Count = 4, FirstUtc = now.AddDays(-4), LastUtc = now.AddHours(-3) },
            };

            var context = new GalaxyContext { SimAvg = 0.62f, SimMin = 0.51f, FinalSpeed = 0.74f, AlertsInWindow = 7 };

            return await SendGalaxyAsync(sample, context, true).ConfigureAwait(false) ?? "Galaxy Offenders test message sent.";
        }

        static string ThreatLevel(float simAvg, out int color)
        {
            if (simAvg < 0.5f) { color = 0xE74C3C; return "CRITICAL"; }
            if (simAvg < 0.75f) { color = 0xE67E22; return "SEVERE"; }
            if (simAvg < 0.9f) { color = 0xF1C40F; return "ELEVATED"; }
            color = 0x3498DB;
            return "ADVISORY";
        }

        static string StatusFor(OffenderHistory.Offender o, SimWatchConfig config)
        {
            if (o.Count < config.GalaxyGpsThreshold) return "ON REGISTER";
            return o.Category == "grids" ? "BEACON LOCKED" : "ESCALATED";
        }

        /// <summary>
        /// The bulletin as an in-universe incident record: a main transmission (header, anomalies, case files for structures and
        /// scripts, advisory) followed by one pilot file per player, each with the pilot badge as its icon.
        /// </summary>
        static List<Embed> BuildGalaxyEmbeds(SimWatchConfig config, IList<OffenderHistory.Offender> offenders, GalaxyContext context, bool isTest)
        {
            var now = DateTime.UtcNow;
            var level = ThreatLevel(context.SimAvg, out var levelColor);

            var cases = offenders.Where(o => o.Category != "players").ToList();
            var pilots = offenders.Where(o => o.Category == "players").ToList();

            var sb = new StringBuilder();

            // header block
            sb.Append("```yaml\n");
            sb.Append("authority    : Galactic Traffic & Systems Authority\n");
            sb.Append("record       : GOA-").Append(UkLocal(now).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)).Append(" // incident ledger\n");
            if (!string.IsNullOrEmpty(config.SectorName)) sb.Append("sector       : ").Append(Clean(config.SectorName)).Append('\n');
            sb.Append("issued       : ").Append(Uk(now)).Append('\n');
            sb.Append("threat level : ").Append(level.ToLowerInvariant()).Append('\n');
            sb.Append("mission      : ").Append(context.FinalSpeed >= 0.995f ? "monitoring, simulation stabilised" : "containment review, simulation degraded").Append('\n');
            if (isTest) sb.Append("note         : test transmission, sample entries\n");
            sb.Append("```\n");

            // briefing
            sb.Append("*All stations and flight crews, this is Sector Command.* Long-range telemetry has logged **")
              .Append(context.AlertsInWindow).Append(" simulation-drag event").Append(context.AlertsInWindow == 1 ? "" : "s").Append("** in the last ")
              .Append(config.RepeatWindowDays).Append(" days. The signatures below were the primary source in **")
              .Append(config.GalaxyThreshold).Append("+ separate events** each and are now entered on the **Offenders Register**.\n\n");

            // detected anomalies
            sb.Append("**\u258C DETECTED ANOMALIES**\n");
            sb.Append("\u25B8 Last event: sim speed fell to **").Append(context.SimAvg.ToString("0.00", CultureInfo.InvariantCulture))
              .Append("** (low ").Append(context.SimMin.ToString("0.00", CultureInfo.InvariantCulture)).Append(") against a nominal 1.00\n");
            sb.Append("\u25B8 Reading at end of diagnostics: **").Append(context.FinalSpeed.ToString("0.00", CultureInfo.InvariantCulture)).Append("**\n");
            sb.Append("\u25B8 Register: **").Append(offenders.Count).Append("** signature").Append(offenders.Count == 1 ? "" : "s")
              .Append(" (").Append(cases.Count).Append(" structure/script, ").Append(pilots.Count).Append(" pilot)\n\n");

            // case files: structures and scripts
            if (cases.Count > 0)
            {
                sb.Append("**\u258C CASE FILES**\n");
                var listed = cases.Take(MaxCaseFiles).ToList();
                for (var i = 0; i < listed.Count; i++)
                {
                    var o = listed[i];
                    sb.Append("**CASE ").Append((i + 1).ToString("00", CultureInfo.InvariantCulture)).Append("** \u25B8 ").Append(GalaxyIcon(o.Category))
                      .Append(" **").Append(MdEscape(o.Display)).Append("** \u00B7 ").Append(GalaxyKind(o.Category)).Append('\n');
                    sb.Append("Incidents **").Append(o.Count).Append("** \u00B7 first ").Append(Uk(o.FirstUtc)).Append(" \u00B7 last ").Append(Uk(o.LastUtc))
                      .Append(" \u00B7 status **").Append(StatusFor(o, config)).Append("**\n");

                    if (o.Category == "grids" && o.Count >= config.GalaxyGpsThreshold && o.Gps != null)
                    {
                        sb.Append("\U0001F4CD Beacon fix: ")
                          .Append(o.Gps.Length > 0 ? "`" + o.Gps.Replace("`", "") + "`" : "*unresolved (no grid with that name was found; it may be concealed or renamed)*")
                          .Append('\n');
                    }
                }

                if (cases.Count > listed.Count) sb.Append("*...and ").Append(cases.Count - listed.Count).Append(" more case file(s) on the register.*\n");
                sb.Append('\n');
            }

            // pilots beyond the embeds that fit
            if (pilots.Count > MaxPilotFiles)
            {
                sb.Append("**Further pilot files:** ")
                  .Append(string.Join(", ", pilots.Skip(MaxPilotFiles).Select(p => MdEscape(p.Display) + " (" + p.Count + ")"))).Append("\n\n");
            }

            // advisory
            sb.Append("**\u258C OPERATOR ADVISORY**\n");
            sb.Append("Audit the flagged assets: reduce block and script load, split oversized structures, or move them clear of the active field. ");
            sb.Append("Entries marked **BEACON LOCKED** or **ESCALATED** take priority. Concealment or removal is at administrator discretion.\n");
            sb.Append("*Automated advisory from profiler telemetry: a load reading, not a finding of fault.*");

            var embeds = new List<Embed>
            {
                new Embed
                {
                    Title = (isTest ? "(TEST) " : "") + "GALAXY OFFENDERS // INCIDENT RECORD",
                    Description = Truncate(sb.ToString(), 3300),
                    Color = levelColor,
                    Footer = FooterText,
                    Timestamp = now.ToString("o", CultureInfo.InvariantCulture),
                    Thumbnail = GalaxyImage != null ? "attachment://" + GalaxyImageName : null,
                },
            };

            // one pilot file per player, badge as icon
            foreach (var pilot in pilots.Take(MaxPilotFiles))
            {
                var file = new StringBuilder();
                file.Append("**Incidents:** ").Append(pilot.Count).Append("  \u00B7  **Status:** ").Append(StatusFor(pilot, config)).Append('\n');
                file.Append("**First contact:** ").Append(Uk(pilot.FirstUtc)).Append('\n');
                file.Append("**Last contact:** ").Append(Uk(pilot.LastUtc)).Append('\n');
                file.Append("*Telemetry attributes recurring simulation load to this pilot's assets or activity. Review it with the pilot; no sanction is implied.*");

                var name = Clean(pilot.Display);
                embeds.Add(new Embed
                {
                    Title = "PILOT FILE // " + (name.Length > 60 ? name.Substring(0, 57) + "..." : name),
                    Description = Truncate(file.ToString(), 700),
                    Color = PilotColor,
                    Thumbnail = PilotBadge != null ? "attachment://" + PilotBadgeName : null,
                });
            }

            return embeds;
        }

        static string GalaxyIcon(string category)
        {
            switch (category)
            {
                case "grids": return "\U0001F6F0\uFE0F";
                case "scripts": return "\u2699\uFE0F";
                default: return "\u2604\uFE0F";
            }
        }

        static string GalaxyKind(string category)
        {
            switch (category)
            {
                case "grids": return "structure";
                case "scripts": return "script";
                case "players": return "pilot";
                default: return category;
            }
        }

        // keeps names from turning into Discord formatting (mentions are blocked separately by allowed_mentions)
        static string MdEscape(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in Clean(text))
            {
                if ("\\*_~`|>[]()".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- layout ----

        static List<List<Embed>> BuildMessages(DiscordReport report)
        {
            var content = new List<Embed>();

            // header block
            var header = new StringBuilder("```yaml\n");
            var width = report.Header.Count == 0 ? 0 : report.Header.Max(h => h.Key.Length);
            foreach (var item in report.Header)
            {
                header.Append(item.Key.PadRight(width)).Append(" : ").Append(Clean(item.Value)).Append('\n');
            }
            header.Append("```");
            content.Add(new Embed
            {
                Title = "\u258CSIMWATCH // " + report.Kind,
                Description = header.ToString(),
                Color = report.Color,
                Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            });

            // estimated cause of the slowdown
            if (report.Estimate.Count > 0)
            {
                var yaml = new StringBuilder("```yaml\n");
                var keyWidth = report.Estimate.Max(e => e.Key.Length);
                foreach (var item in report.Estimate)
                {
                    yaml.Append(item.Key.PadRight(keyWidth)).Append(" : ").Append(Clean(item.Value)).Append('\n');
                }
                yaml.Append("```");

                content.Add(new Embed
                {
                    Title = "\u25B8 estimated cause",
                    Description = Truncate(yaml.ToString(), 4000),
                    Color = report.Color,
                });
            }

            // one code block per command (split if the output is long)
            foreach (var section in report.Sections)
            {
                var body = string.IsNullOrWhiteSpace(section.Body) ? "(no output)" : Clean(section.Body);
                var chunks = Split(body, MaxCodeChars);

                for (var i = 0; i < chunks.Count; i++)
                {
                    var title = "\u25B8 " + section.Title + (chunks.Count > 1 ? $" ({i + 1}/{chunks.Count})" : "");
                    content.Add(new Embed
                    {
                        Title = Truncate(title, 250),
                        Description = "```\n" + chunks[i] + "\n```",
                        Color = Cyan,
                    });
                }
            }

            var closing = new Embed
            {
                Title = "\u25B8 end of report",
                Description = "```yaml\n" + Clean(report.Closing) + "\n```",
                Color = report.Color,
                Footer = FooterText,
            };

            var messages = Pack(content);

            if (messages.Count > MaxMessages - 1)
            {
                messages = messages.Take(MaxMessages - 1).ToList();
                messages.Add(new List<Embed>
                {
                    new Embed
                    {
                        Title = "\u25B8 output truncated",
                        Description = "```\nToo long for Discord. The full report is saved on the server.\n```",
                        Color = Orange,
                    },
                    closing,
                });
            }
            else
            {
                var last = messages.Count > 0 ? messages[messages.Count - 1] : null;
                if (last != null && last.Count < MaxEmbedsPerMessage && last.Sum(e => e.Length) + closing.Length <= MaxMessageChars)
                    last.Add(closing);
                else
                    messages.Add(new List<Embed> { closing });
            }

            return messages;
        }

        static List<List<Embed>> Pack(List<Embed> embeds)
        {
            var messages = new List<List<Embed>>();
            var current = new List<Embed>();
            var size = 0;

            foreach (var embed in embeds)
            {
                if (current.Count > 0 && (current.Count >= MaxEmbedsPerMessage || size + embed.Length > MaxMessageChars))
                {
                    messages.Add(current);
                    current = new List<Embed>();
                    size = 0;
                }

                current.Add(embed);
                size += embed.Length;
            }

            if (current.Count > 0) messages.Add(current);
            return messages;
        }

        static List<string> Split(string text, int max)
        {
            var chunks = new List<string>();
            var sb = new StringBuilder();

            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw;

                while (line.Length > max) // a single very long line
                {
                    if (sb.Length > 0) { chunks.Add(sb.ToString().TrimEnd('\n')); sb.Clear(); }
                    chunks.Add(line.Substring(0, max));
                    line = line.Substring(max);
                }

                if (sb.Length > 0 && sb.Length + line.Length + 1 > max)
                {
                    chunks.Add(sb.ToString().TrimEnd('\n'));
                    sb.Clear();
                }

                sb.Append(line).Append('\n');
            }

            if (sb.Length > 0) chunks.Add(sb.ToString().TrimEnd('\n'));

            chunks = chunks.Where(c => c.Length > 0).ToList();
            if (chunks.Count == 0) chunks.Add("(no output)");
            return chunks;
        }

        // keeps text from closing the code block early
        static string Clean(string text)
        {
            return (text ?? "").Replace("```", "'''").TrimEnd();
        }

        // ---- HTTP ----

        /// <summary>"&lt;@&amp;roleId&gt; text" when a role to ping is set, otherwise null.</summary>
        static string PingText(SimWatchConfig config, string what)
        {
            if (string.IsNullOrEmpty(config.DiscordPingRoleId)) return null;

            var sector = string.IsNullOrEmpty(config.SectorName) ? "" : " - " + config.SectorName;
            return $"<@&{config.DiscordPingRoleId}> {what}{sector}";
        }

        static string BuildPayload(SimWatchConfig config, List<Embed> embeds, string pingText = null, string usernameOverride = null)
        {
            var sb = new StringBuilder("{");
            var username = usernameOverride ?? config.DiscordUsername;
            if (!string.IsNullOrWhiteSpace(username))
            {
                sb.Append("\"username\":").Append(JsonString(username)).Append(',');
            }

            if (!string.IsNullOrEmpty(pingText))
            {
                // only this one role may be mentioned; everything else stays silent
                sb.Append("\"content\":").Append(JsonString(pingText)).Append(',');
                sb.Append("\"allowed_mentions\":{\"parse\":[],\"roles\":[\"").Append(config.DiscordPingRoleId).Append("\"]},");
            }
            else
            {
                sb.Append("\"allowed_mentions\":{\"parse\":[]},");
            }

            sb.Append("\"embeds\":[");

            for (var i = 0; i < embeds.Count; i++)
            {
                var e = embeds[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"title\":").Append(JsonString(e.Title));
                sb.Append(",\"description\":").Append(JsonString(e.Description));
                sb.Append(",\"color\":").Append(e.Color.ToString(CultureInfo.InvariantCulture));
                if (e.Timestamp != null) sb.Append(",\"timestamp\":").Append(JsonString(e.Timestamp));
                if (e.Thumbnail != null) sb.Append(",\"thumbnail\":{\"url\":").Append(JsonString(e.Thumbnail)).Append('}');
                if (e.Footer != null) sb.Append(",\"footer\":{\"text\":").Append(JsonString(e.Footer)).Append('}');
                sb.Append('}');
            }

            return sb.Append("]}").ToString();
        }

        /// <returns>null on success, otherwise an error description. Retries when Discord rate limits us.</returns>
        static async Task<string> PostAsync(string url, string payloadJson, string fileName, string fileText, IList<KeyValuePair<string, byte[]>> images = null)
        {
            const int attempts = 4;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                using (var content = BuildContent(payloadJson, fileName, fileText, images))
                using (var response = await Http.PostAsync(url, content).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode) return null;

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if ((int)response.StatusCode == 429 && attempt < attempts)
                    {
                        await Task.Delay(RetryDelay(response, body)).ConfigureAwait(false);
                        continue;
                    }

                    return $"Discord returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 300)}";
                }
            }

            return "rate limited by Discord";
        }

        static HttpContent BuildContent(string payloadJson, string fileName, string fileText, IList<KeyValuePair<string, byte[]>> images)
        {
            var hasImages = images != null && images.Count > 0;
            if (fileText == null && !hasImages) return new StringContent(payloadJson, Encoding.UTF8, "application/json");

            var form = new MultipartFormDataContent();
            form.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json");

            var index = 0;
            if (fileText != null)
            {
                var file = new ByteArrayContent(Encoding.UTF8.GetBytes(fileText));
                file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                form.Add(file, $"files[{index++}]", fileName ?? "report.txt");
            }

            if (hasImages)
            {
                foreach (var image in images)
                {
                    var picture = new ByteArrayContent(image.Value);
                    picture.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    form.Add(picture, $"files[{index++}]", image.Key);
                }
            }

            return form;
        }

        static TimeSpan RetryDelay(HttpResponseMessage response, string body)
        {
            var seconds = 2.0;

            var delta = response.Headers.RetryAfter?.Delta;
            if (delta.HasValue)
            {
                seconds = delta.Value.TotalSeconds;
            }
            else
            {
                var match = RetryAfterRegex.Match(body ?? "");
                if (match.Success) double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            }

            return TimeSpan.FromSeconds(Math.Min(30, Math.Max(0.5, seconds)) + 0.25);
        }

        static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max - 3) + "...";
        }

        internal static string JsonString(string text)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in text ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
