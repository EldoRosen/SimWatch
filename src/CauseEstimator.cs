using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SimWatch
{
    /// <summary>
    /// Rule-of-thumb guess at why the sim speed is low, from the sim speed samples, the server's CPU use and the
    /// heaviest lines the Profiler printed. It is an estimate: it never replaces reading the full report.
    /// </summary>
    public static class CauseEstimator
    {
        static readonly Regex Ranked = new Regex(@"\d+(?:\.\d+)?\s*(?:ms|%)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Millis = new Regex(@"(\d+(?:\.\d+)?)\s*ms", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Order used to name a suspect when the profiler lines carry no milliseconds to compare.
        static readonly string[] Priority = { "grids", "scripts", "entities", "blocktypes", "session", "players", "frames" };

        public static List<KeyValuePair<string, string>> Estimate(SimWatchConfig config, SimSample sample, ProfilerInfo profiler, IList<ReportSection> sections)
        {
            var items = new List<KeyValuePair<string, string>>();
            void Add(string key, string value) => items.Add(new KeyValuePair<string, string>(key, value));
            string N(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

            var low = sample.Avg < config.SimSpeedThreshold;

            // heaviest line per profile command, and what went wrong with the others
            var tops = new List<KeyValuePair<string, string>>();
            var problems = new List<string>();
            string physicsText = null;

            foreach (var section in sections)
            {
                var name = ProfileName(section.Title);
                if (name == null) continue;

                if (section.Title.IndexOf("--inspect=", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                if (section.Status == "skipped") continue;
                if (section.Status != "completed")
                {
                    problems.Add($"{name} {section.Status.Replace('_', ' ')}");
                    continue;
                }

                if (name == "physics")
                {
                    physicsText = section.Body;
                    continue;
                }

                var line = TopLine(section.Body);
                if (line != null) tops.Add(new KeyValuePair<string, string>(name, line));
            }

            // physics: how big is the top cluster
            string physics = null;
            var largePhysics = false;
            if (physicsText != null)
            {
                var match = SimWatcher.TopClusterRegex.Match(physicsText);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var entities))
                {
                    largePhysics = entities >= config.InspectEntityThreshold;
                    physics = largePhysics
                        ? $"cluster 0 holds {entities} entities (large: physics load likely)"
                        : $"cluster 0 holds {entities} entities (small)";
                }
            }

            // main suspect: the largest millisecond figure, else the first category in a fixed order
            string suspect = null;
            var withMs = tops
                .Select(t => new { t.Key, t.Value, Ms = Millis.Match(t.Value) })
                .Where(t => t.Ms.Success)
                .Select(t => new { t.Key, t.Value, Value_ms = double.Parse(t.Ms.Groups[1].Value, CultureInfo.InvariantCulture) })
                .OrderByDescending(t => t.Value_ms)
                .FirstOrDefault();

            if (withMs != null)
            {
                suspect = $"{withMs.Key}: {withMs.Value}";
            }
            else
            {
                foreach (var name in Priority)
                {
                    var found = tops.FirstOrDefault(t => t.Key == name);
                    if (found.Key != null) { suspect = $"{found.Key}: {found.Value}"; break; }
                }
            }

            if (suspect == null && largePhysics) suspect = "physics: " + physics;

            // verdict
            string verdict;
            if (!low)
                verdict = $"no slowdown during the sample (avg {N(sample.Avg)} is not below {N(config.SimSpeedThreshold)}); forced run, figures are for reference";
            else if (sample.CoresBusy < 0.5)
                verdict = "server process mostly idle while sim speed is low: likely waiting (disk/IO, GC pause, network) or another program taking CPU";
            else
                verdict = "simulation is CPU-bound (main thread overloaded)";

            Add("verdict", verdict);

            if (low)
            {
                var spiky = sample.Min < sample.Avg * 0.6f;
                Add("pattern", (spiky ? "spiky, short dips well below the average (stalls, GC, saves, big events)" : "sustained, steady low speed")
                    + $" [min {N(sample.Min)} / avg {N(sample.Avg)} / max {N(sample.Max)}]");
            }

            Add("cpu", $"{N(sample.CoresBusy)} cores busy of {Environment.ProcessorCount} logical, working set {sample.WorkingSetMb:0} MB");

            if (suspect != null && sample.CoresBusy >= 0.5) Add("top_suspect", suspect);
            if (physics != null) Add("physics", physics);

            foreach (var top in tops) Add("top_" + top.Key, top.Value);

            if (profiler.State == ProfilerState.Missing) problems.Add("Profiler plugin missing, no profile data");
            if (problems.Count > 0) Add("caveats", string.Join("; ", problems));

            Add("note", "estimate from sim speed, CPU use and the top profiler lines; check the full report to confirm");
            return items;
        }

        /// <summary>The heaviest line of every finished `!profile` ranking (command name, line). Physics and --inspect are left out.</summary>
        public static List<KeyValuePair<string, string>> TopFindings(IList<ReportSection> sections)
        {
            var tops = new List<KeyValuePair<string, string>>();

            foreach (var section in sections)
            {
                var name = ProfileName(section.Title);
                if (name == null || name == "physics" || section.Status != "completed") continue;
                if (section.Title.IndexOf("--inspect=", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var line = TopLine(section.Body);
                if (line != null) tops.Add(new KeyValuePair<string, string>(name, line));
            }

            return tops;
        }

        // "!profile grids --secs=15" -> "grids"
        static string ProfileName(string command)
        {
            var parts = (command ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "!profile", StringComparison.OrdinalIgnoreCase)) return null;
            return parts[1].ToLowerInvariant();
        }

        // first ranked result line (contains a ms or % figure); skips the "Started/Finished profiling" banners and notes
        static string TopLine(string body)
        {
            foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("(")) continue;
                if (line.IndexOf("profiling", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!Ranked.IsMatch(line)) continue;
                return line.Length > 110 ? line.Substring(0, 107) + "..." : line;
            }
            return null;
        }
    }
}
