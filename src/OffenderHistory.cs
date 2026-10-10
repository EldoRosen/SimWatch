using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NLog;

namespace SimWatch
{
    /// <summary>
    /// Remembers the top result of each profile command (grid, script, player, ...) for every low-sim-speed alert in a small
    /// file next to the reports (history.tsv, not touched by the report cleanup) and flags names that keep coming back.
    /// </summary>
    public sealed class OffenderHistory
    {
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        // Categories that name a concrete object. frames/session/entities/blocktypes are almost always the same kind of
        // thing at the top, so repeating there says nothing.
        static readonly HashSet<string> IgnoredCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "frames", "session", "entities", "blocktypes", "physics",
        };

        const int MaxStoredAlerts = 200;
        static readonly TimeSpan MissRetry = TimeSpan.FromHours(6); // a grid GPS that could not be resolved is looked up again after this
        static readonly TimeSpan MaxStoredAge = TimeSpan.FromDays(90);

        static readonly Regex RankPrefix = new Regex(@"^\s*(?:#\s*\d+|\d+\s*[.):]|\[\d+\])\s*", RegexOptions.Compiled);
        static readonly Regex Measurement = new Regex(@"-?(?:\d+[.,]\d+\s*s\b|\d+(?:[.,]\d+)?\s*(?:%|(?:ms|\u00B5s|us|ticks?|tps)\b)(?:\s*/\s*\w+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Decimal = new Regex(@"-?\b\d+[.,]\d+\b", RegexOptions.Compiled);
        static readonly Regex EmptyBrackets = new Regex(@"\(\s*\)|\[\s*\]", RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);
        static readonly char[] EdgeChars = { ' ', '\t', ':', '|', ',', ';', '=', '-', '>', '<', '(', ')', '[', ']' };

        sealed class Entry
        {
            public long AlertId;     // UTC ticks of the alert
            public string Category;
            public string Key;       // lower-cased identity, used for comparing
            public string Display;   // identity as printed
        }

        /// <summary>A name that was the top result in several alerts: an entry of the Galaxy Offenders register.</summary>
        public sealed class Offender
        {
            public string Category;
            public string Key;
            public string Display;
            public int Count;
            public DateTime FirstUtc;
            public DateTime LastUtc;
            public string Gps;   // null = not looked up, empty = looked up but the grid was not found
        }

        readonly string _path;
        readonly string _announcedPath;
        readonly object _lock = new object();

        public OffenderHistory(string directory)
        {
            _path = Path.Combine(directory, "history.tsv");
            _announcedPath = Path.Combine(directory, "galaxy-announced.tsv");
        }

        /// <summary>The name part of a profile line: rank, timings and percentages removed. Null if nothing useful is left.</summary>
        public static string Identity(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            var text = RankPrefix.Replace(line, "");
            text = Measurement.Replace(text, " ");
            text = Decimal.Replace(text, " ");
            text = EmptyBrackets.Replace(text, " ");
            text = Spaces.Replace(text, " ").Trim(EdgeChars);

            if (text.Length < 2 || text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c))) return null;
            return text.Length > 60 ? text.Substring(0, 57) + "..." : text;
        }

        /// <summary>
        /// Adds this alert to the history (when <paramref name="record"/> is set) and returns report lines describing names that
        /// were the top result of the same profile command in several of the latest alerts. Never throws.
        /// </summary>
        public List<KeyValuePair<string, string>> Analyse(SimWatchConfig config, IList<KeyValuePair<string, string>> tops, DateTime nowUtc, bool record)
        {
            var lines = new List<KeyValuePair<string, string>>();
            var wantFlag = config.RepeatLookbackAlerts > 0;
            if (!wantFlag && !config.GalaxyEnabled) return lines;   // nothing uses the history
            if (!record) return lines; // test runs and runs with normal sim speed are neither recorded nor flagged

            try
            {
                lock (_lock)
                {
                    var all = Load();
                    var alertId = nowUtc.Ticks;

                    if (record)
                    {
                        foreach (var top in tops)
                        {
                            if (IgnoredCategories.Contains(top.Key)) continue;
                            var identity = Identity(top.Value);
                            if (identity == null) continue;

                            all.Add(new Entry { AlertId = alertId, Category = top.Key, Key = identity.ToLowerInvariant(), Display = identity });
                        }

                        Save(Prune(all, nowUtc));
                    }

                    if (!wantFlag) return lines; // history is kept for the Galaxy Offenders register only

                    // the latest alerts inside the time window, newest first
                    var windowStart = nowUtc.AddDays(-config.RepeatWindowDays).Ticks;
                    var alertIds = all.Where(e => e.AlertId >= windowStart)
                        .Select(e => e.AlertId).Distinct()
                        .OrderByDescending(id => id)
                        .Take(config.RepeatLookbackAlerts)
                        .ToList();

                    if (record && !alertIds.Contains(alertId)) alertIds.Insert(0, alertId); // this alert had no usable names, still counts as an alert

                    var considered = alertIds.Count;
                    var found = false;

                    foreach (var current in all.Where(e => e.AlertId == alertId))
                    {
                        var hits = alertIds
                            .Where(id => all.Any(e => e.AlertId == id && e.Category == current.Category && e.Key == current.Key))
                            .OrderBy(id => id)
                            .ToList();

                        if (hits.Count < 2) continue;

                        found = true;
                        var since = new DateTime(hits[0], DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                        lines.Add(new KeyValuePair<string, string>("repeat_offender",
                            $"{current.Category}: \"{current.Display}\" was the top {current.Category} result in {hits.Count} of the last {considered} alerts (first seen {since})"));
                    }

                    if (!found)
                    {
                        lines.Add(new KeyValuePair<string, string>("repeat_offender", considered <= 1
                            ? "no earlier alerts on record to compare with"
                            : $"none, the top results differ across the last {considered} alerts"));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SimWatch: repeat offender check failed");
                lines.Clear();
            }

            return lines;
        }

        /// <summary>How many alerts are on record inside the window (alerts without a usable name aren't stored).</summary>
        public int CountAlerts(SimWatchConfig config, DateTime nowUtc)
        {
            try
            {
                lock (_lock)
                {
                    var windowStart = nowUtc.AddDays(-config.RepeatWindowDays).Ticks;
                    return Load().Where(e => e.AlertId >= windowStart).Select(e => e.AlertId).Distinct().Count();
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>The current register: names that were the top result in at least GalaxyThreshold alerts inside the window, most frequent first.</summary>
        public List<Offender> GetOffenders(SimWatchConfig config, DateTime nowUtc)
        {
            try
            {
                lock (_lock)
                {
                    return BuildOffenders(Load(), config, nowUtc);
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SimWatch: could not build the offender register");
                return new List<Offender>();
            }
        }

        static List<Offender> BuildOffenders(List<Entry> all, SimWatchConfig config, DateTime nowUtc)
        {
            var windowStart = nowUtc.AddDays(-config.RepeatWindowDays).Ticks;

            return all
                .Where(e => e.AlertId >= windowStart && !IgnoredCategories.Contains(e.Category))
                .GroupBy(e => new { e.Category, e.Key })
                .Select(g => new
                {
                    g.Key.Category,
                    g.Key.Key,
                    Ids = g.Select(e => e.AlertId).Distinct().OrderBy(id => id).ToList(),
                    Latest = g.OrderByDescending(e => e.AlertId).First(),
                })
                .Where(g => g.Ids.Count >= config.GalaxyThreshold)
                .Select(g => new Offender
                {
                    Category = g.Category,
                    Key = g.Key,
                    Display = g.Latest.Display,
                    Count = g.Ids.Count,
                    FirstUtc = new DateTime(g.Ids[0], DateTimeKind.Utc),
                    LastUtc = new DateTime(g.Ids[g.Ids.Count - 1], DateTimeKind.Utc),
                })
                .OrderByDescending(o => o.Count)
                .ThenByDescending(o => o.LastUtc)
                .ToList();
        }

        /// <summary>
        /// The register, plus whether it holds a name that has not been announced yet (an announcement counts for the length of
        /// the window, after that a name that is still an offender is announced again).
        /// </summary>
        public List<Offender> PendingAnnouncement(SimWatchConfig config, DateTime nowUtc, out bool hasNew)
        {
            hasNew = false;

            try
            {
                lock (_lock)
                {
                    var offenders = BuildOffenders(Load(), config, nowUtc);
                    var announced = LoadAnnounced(config, nowUtc);
                    hasNew = offenders.Any(o => !IsAnnounced(announced, o, config));
                    return offenders;
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SimWatch: could not check the Galaxy Offenders register");
                return new List<Offender>();
            }
        }

        static bool WantsGps(Offender o, SimWatchConfig config) => o.Category == "grids" && o.Count >= config.GalaxyGpsThreshold;

        // a name announced before is not new again, except a grid that has since reached the GPS level
        static bool IsAnnounced(HashSet<string> announced, Offender o, SimWatchConfig config)
        {
            var key = o.Category + "\t" + o.Key;
            if (WantsGps(o, config)) return announced.Contains(key + "\tgps") || announced.Contains(key + "\tmiss");
            return announced.Contains(key + "\treg") || announced.Contains(key + "\tgps");
        }

        public void MarkAnnounced(IEnumerable<Offender> offenders, SimWatchConfig config, DateTime nowUtc)
        {
            try
            {
                lock (_lock)
                {
                    var lines = new List<string>();
                    if (File.Exists(_announcedPath)) lines.AddRange(File.ReadAllLines(_announcedPath, Encoding.UTF8));

                    var already = LoadAnnounced(config, nowUtc);
                    foreach (var o in offenders)
                    {
                        // names announced before keep their original time, so they come round again after the window
                        if (IsAnnounced(already, o, config)) continue;

                        // a grid whose position couldn't be found is tried again after MissRetry; the others are done for the window
                        var level = !WantsGps(o, config) ? "reg" : string.IsNullOrEmpty(o.Gps) ? "miss" : "gps";
                        lines.Add($"{Clean(o.Category)}\t{Clean(o.Key)}\t{nowUtc.Ticks.ToString(CultureInfo.InvariantCulture)}\t{level}");
                    }

                    // keep only the latest 500 lines
                    var keep = lines.Skip(Math.Max(0, lines.Count - 500)).ToList();
                    Directory.CreateDirectory(Path.GetDirectoryName(_announcedPath));
                    File.WriteAllText(_announcedPath, string.Join("\n", keep) + "\n", Encoding.UTF8);
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SimWatch: could not remember the announced offenders");
            }
        }

        HashSet<string> LoadAnnounced(SimWatchConfig config, DateTime nowUtc)
        {
            var set = new HashSet<string>();
            if (!File.Exists(_announcedPath)) return set;

            var windowStart = nowUtc.AddDays(-config.RepeatWindowDays).Ticks;
            foreach (var line in File.ReadAllLines(_announcedPath, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) continue;
                var level = parts.Length > 3 ? parts[3] : "reg";
                var start = level == "miss" ? Math.Max(windowStart, nowUtc.Ticks - MissRetry.Ticks) : windowStart;
                if (ticks >= start) set.Add(parts[0] + "\t" + parts[1] + "\t" + level);
            }

            return set;
        }

        List<Entry> Load()
        {
            var entries = new List<Entry>();
            if (!File.Exists(_path)) return entries;

            foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                if (parts.Length < 4 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;

                entries.Add(new Entry { AlertId = id, Category = parts[1], Key = parts[2], Display = parts[3] });
            }

            return entries;
        }

        static List<Entry> Prune(List<Entry> entries, DateTime nowUtc)
        {
            var oldest = nowUtc - MaxStoredAge;
            var keepIds = new HashSet<long>(entries
                .Select(e => e.AlertId).Distinct()
                .Where(id => id >= oldest.Ticks)
                .OrderByDescending(id => id)
                .Take(MaxStoredAlerts));

            return entries.Where(e => keepIds.Contains(e.AlertId)).ToList();
        }

        void Save(List<Entry> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));

            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                sb.Append(e.AlertId.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(Clean(e.Category)).Append('\t')
                  .Append(Clean(e.Key)).Append('\t')
                  .Append(Clean(e.Display)).Append('\n');
            }

            File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
        }

        static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
