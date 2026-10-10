using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Multiplayer;
using Torch.API;
using Torch.API.Managers;
using Torch.Commands;

namespace SimWatch
{
    public sealed class SimSample
    {
        public float Min { get; set; }
        public float Avg { get; set; }
        public float Max { get; set; }
        public int Seconds { get; set; }
        public double CoresBusy { get; set; }
        public double WorkingSetMb { get; set; }
    }

    public sealed class CheckOutcome
    {
        public SimSample Sample { get; set; }
        public bool DiagnosticsRan { get; set; }
        public string ReportPath { get; set; }
        public bool DiscordSent { get; set; }
        public string DiscordError { get; set; }
        public string DiscordSkipped { get; set; }   // why the Discord post was left out, if it was
    }

    sealed class CommandResult
    {
        public double Seconds { get; set; }   // how long it took
        public string Output { get; set; }  // what the command printed
        public string Text { get; set; }    // Output plus notes about timeouts etc., for the report
        public WaitResult Status { get; set; }
    }

    /// <summary>
    /// Two optional triggers, both running the configured diagnostic commands only when the averaged
    /// sim speed is below the threshold: a timer (every N minutes, N = 0 turns it off) and a live monitor
    /// (watches every second, with a cooldown). The output goes into a report file and optionally to Discord.
    /// </summary>
    public sealed class SimWatcher
    {
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();
        static readonly Regex SecsRegex = new Regex(@"--secs=(\d{1,6})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex TicsRegex = new Regex(@"--tics=(\d{1,7})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Profiler prints the physics clusters as "<index>: <n> entities in <size>km: ...", top cluster first.
        internal static readonly Regex TopClusterRegex = new Regex(@"^\s*0:\s*(\d+)\s+entities", RegexOptions.Multiline | RegexOptions.Compiled);

        static readonly Regex ResultLineRegex = new Regex(@"\d+(?:\.\d+)?\s*(?:ms|%)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        const string InspectCommand = "!profile physics --inspect=0";
        const int DefaultProfileSeconds = 10; // Profiler's default when --secs is omitted
        static readonly TimeSpan LiveWarmup = TimeSpan.FromMinutes(2); // ignore the live monitor while the server is still starting up

        struct Reading
        {
            public float Ratio;
            public double CpuSeconds;
            public DateTime Utc;
        }

        readonly ITorchBase _torch;
        readonly Func<SimWatchConfig> _getConfig;
        readonly string _reportDir;
        readonly OffenderHistory _history;
        readonly DiscordNotifier _notifier;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public SimWatcher(ITorchBase torch, Func<SimWatchConfig> getConfig, string reportDir, DiscordNotifier notifier)
        {
            _torch = torch;
            _getConfig = getConfig;
            _reportDir = reportDir;
            _history = new OffenderHistory(reportDir);
            _notifier = notifier;
        }

        public bool IsBusy => _gate.CurrentCount == 0;
        public DateTime? LastCheckUtc { get; private set; }
        long _lastDiagnosticsTicks;                       // 0 = never; read/written from several threads
        volatile float _liveAverage = float.NaN;          // NaN = live monitor off or still filling its window

        public DateTime? LastDiagnosticsUtc
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastDiagnosticsTicks);
                return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>Rolling average of the live monitor; null while it is off or filling up.</summary>
        public float? LiveAverage => float.IsNaN(_liveAverage) ? (float?)null : _liveAverage;

        void MarkDiagnosticsFinished() => Interlocked.Exchange(ref _lastDiagnosticsTicks, DateTime.UtcNow.Ticks);
        public SimSample LastSample { get; private set; }
        public string LastReportPath { get; private set; }
        public string ReportDirectory => _reportDir;

        public List<OffenderHistory.Offender> GetOffenders() => _history.GetOffenders(_getConfig(), DateTime.UtcNow);

        public ProfilerInfo GetProfilerInfo() => ProfilerDetector.Detect(_torch);
        public ProfilerInfo GetConcealmentInfo() => ProfilerDetector.DetectPlugin(_torch, "Concealment");

        public async Task RunLoopAsync(CancellationToken ct)
        {
            Log.Info("SimWatch started");

            var config = _getConfig();
            if (config.CheckIntervalMinutes <= 0 && !config.LiveMonitorEnabled)
            {
                Log.Warn("SimWatch: no timer (interval 0) and the live monitor is off, so nothing runs automatically. Use the !simwatch commands or change the settings.");
            }

            var profiler = ProfilerDetector.Detect(_torch);
            if (profiler.State == ProfilerState.Missing)
            {
                Log.Warn("SimWatch: Profiler check: NOT FOUND. '!profile' commands will be skipped until the Profiler plugin is installed and enabled. " +
                         $"Plugins seen: {string.Join(", ", profiler.Seen)}");
            }
            else
            {
                Log.Info($"SimWatch: Profiler check: {profiler.Describe()}");
            }

            if (config.ConcealAfterDiagnostics)
            {
                var concealment = GetConcealmentInfo();
                if (concealment.State == ProfilerState.Missing)
                    Log.Warn($"SimWatch: Concealment check: NOT FOUND, '{config.ConcealCommand}' will be skipped after diagnostics. Plugins seen: {string.Join(", ", concealment.Seen)}");
                else
                    Log.Info($"SimWatch: Concealment check: {concealment.Describe()}");
            }

            try
            {
                await Task.WhenAll(RunTimerLoopAsync(ct), RunLiveMonitorAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // stopping
            }

            Log.Info("SimWatch stopped");
        }

        /// <summary>Timed check. Does nothing while the interval is 0, and picks up setting changes within seconds.</summary>
        async Task RunTimerLoopAsync(CancellationToken ct)
        {
            DateTime? due = null;
            var lastInterval = 0;
            var lastCleanup = DateTime.MinValue;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);

                    if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                    {
                        lastCleanup = DateTime.UtcNow;
                        CleanupReports();
                    }

                    var config = _getConfig();
                    var interval = config.CheckIntervalMinutes;
                    if (!config.Enabled || interval <= 0)
                    {
                        due = null;
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    if (due == null || interval != lastInterval)
                    {
                        due = now.AddMinutes(interval);
                        lastInterval = interval;
                        continue;
                    }

                    if (now < due.Value) continue;
                    if (IsBusy)
                    {
                        due = now.AddMinutes(interval); // a manual or live run is in progress
                        continue;
                    }

                    await CheckAsync(false, ct);
                    due = DateTime.UtcNow.AddMinutes(_getConfig().CheckIntervalMinutes);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    Log.Error(e, "SimWatch check failed");
                    due = null;
                }
            }
        }

        /// <summary>
        /// Live monitor: one reading per second in a rolling window. When the window is full and its average is
        /// below the threshold, the diagnostics run (at most once per cooldown). No timer involved.
        /// </summary>
        async Task RunLiveMonitorAsync(CancellationToken ct)
        {
            var readings = new Queue<Reading>();
            var started = DateTime.UtcNow;

            using (var process = Process.GetCurrentProcess())
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, ct);

                        var config = _getConfig();
                        if (!config.Enabled || !config.LiveMonitorEnabled)
                        {
                            readings.Clear();
                            _liveAverage = float.NaN;
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        process.Refresh();
                        readings.Enqueue(new Reading
                        {
                            Ratio = Sync.ServerSimulationRatio,
                            CpuSeconds = process.TotalProcessorTime.TotalSeconds,
                            Utc = now,
                        });
                        while (readings.Count > config.SampleSeconds) readings.Dequeue();

                        if (readings.Count < config.SampleSeconds)
                        {
                            _liveAverage = float.NaN;
                            continue;
                        }

                        var average = readings.Average(r => r.Ratio);
                        _liveAverage = average;

                        if (now - started < LiveWarmup) continue;
                        if (average >= config.SimSpeedThreshold) continue;
                        if (IsBusy) continue;
                        if (LastDiagnosticsUtc is DateTime last && now - last < TimeSpan.FromMinutes(config.LiveCooldownMinutes)) continue;

                        var sample = BuildSample(readings, process);
                        readings.Clear();
                        _liveAverage = float.NaN;

                        await RunLiveTriggerAsync(sample, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "SimWatch live monitor failed");
                        readings.Clear();
                    }
                }
            }
        }

        static SimSample BuildSample(Queue<Reading> readings, Process process)
        {
            var first = readings.Peek();
            var last = readings.Last();
            var span = Math.Max(0.001, (last.Utc - first.Utc).TotalSeconds);

            return new SimSample
            {
                Min = readings.Min(r => r.Ratio),
                Avg = readings.Average(r => r.Ratio),
                Max = readings.Max(r => r.Ratio),
                Seconds = readings.Count,
                CoresBusy = (last.CpuSeconds - first.CpuSeconds) / span,
                WorkingSetMb = process.WorkingSet64 / (1024.0 * 1024.0),
            };
        }

        async Task RunLiveTriggerAsync(SimSample sample, CancellationToken ct)
        {
            if (!await _gate.WaitAsync(0, ct)) return; // something else started a run in the meantime
            try
            {
                var config = _getConfig();
                LastSample = sample;
                LastCheckUtc = DateTime.UtcNow;

                Log.Warn($"SimWatch: live monitor: sim speed low (avg {sample.Avg:0.00}, min {sample.Min:0.00}, threshold {config.SimSpeedThreshold:0.00}); running diagnostics");

                var outcome = await RunDiagnosticsAsync(config, sample, true, false, true, ct);
                outcome.Sample = sample;
            }
            finally
            {
                MarkDiagnosticsFinished(); // also after a failed run, so the live monitor can't retrigger in a loop
                _gate.Release();
            }
        }

        /// <param name="force">Run the diagnostics even if the sim speed is fine.</param>
        /// <param name="manualScan">`!simwatch scan`: the report is always sent to Discord when a webhook URL is set.</param>
        public async Task<CheckOutcome> CheckAsync(bool force, CancellationToken ct, bool manualScan = false)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var config = _getConfig();

                var sample = await SampleAsync(config.SampleSeconds, ct);
                LastSample = sample;
                LastCheckUtc = DateTime.UtcNow;

                var isLow = sample.Avg < config.SimSpeedThreshold;
                if (!isLow && !force)
                {
                    Log.Info($"SimWatch: sim speed OK (avg {sample.Avg:0.00}, min {sample.Min:0.00}, threshold {config.SimSpeedThreshold:0.00})");
                    return new CheckOutcome { Sample = sample };
                }

                if (isLow)
                {
                    Log.Warn($"SimWatch: sim speed low (avg {sample.Avg:0.00}, min {sample.Min:0.00}, threshold {config.SimSpeedThreshold:0.00}); running diagnostics");
                }
                else
                {
                    Log.Info(manualScan ? "SimWatch: manual scan" : "SimWatch: forced diagnostics run");
                }

                var outcome = await RunDiagnosticsAsync(config, sample, isLow, manualScan, false, ct);
                outcome.Sample = sample;
                return outcome;
            }
            finally
            {
                _gate.Release();
            }
        }

        static async Task<SimSample> SampleAsync(int seconds, CancellationToken ct)
        {
            using (var process = Process.GetCurrentProcess())
            {
                var cpuStart = process.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                var values = new float[seconds];

                for (var i = 0; i < seconds; i++)
                {
                    values[i] = Sync.ServerSimulationRatio;
                    await Task.Delay(1000, ct);
                }

                process.Refresh();
                var cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;

                return new SimSample
                {
                    Min = values.Min(),
                    Avg = values.Average(),
                    Max = values.Max(),
                    Seconds = seconds,
                    CoresBusy = cpuSeconds / Math.Max(0.001, clock.Elapsed.TotalSeconds),
                    WorkingSetMb = process.WorkingSet64 / (1024.0 * 1024.0),
                };
            }
        }

        async Task<CheckOutcome> RunDiagnosticsAsync(SimWatchConfig config, SimSample sample, bool wasLow, bool manualScan, bool live, CancellationToken ct)
        {
            var now = DateTime.Now;
            var time = now.ToString("yyyy-MM-dd HH:mm:ss zzz");
            var trigger = TriggerText(wasLow, manualScan, live);
            var simLine = $"avg {sample.Avg:0.00}, min {sample.Min:0.00}, max {sample.Max:0.00} over {sample.Seconds}s (threshold {config.SimSpeedThreshold:0.00})";
            var procLine = $"{sample.CoresBusy:0.00} cores busy of {Environment.ProcessorCount} logical, working set {sample.WorkingSetMb:0} MB";

            var profiler = ProfilerDetector.Detect(_torch);
            var concealment = config.ConcealAfterDiagnostics ? GetConcealmentInfo() : null;

            var report = new ReportBuilder();
            report.Text.AppendLine("SimWatch diagnostic report");
            report.Text.AppendLine($"Server:      {Environment.MachineName}");
            if (!string.IsNullOrEmpty(config.SectorName)) report.Text.AppendLine($"Sector:      {config.SectorName}");
            report.Text.AppendLine($"Time:        {time}");
            report.Text.AppendLine($"Trigger:     {trigger}");
            report.Text.AppendLine($"Sim speed:   {simLine}");
            report.Text.AppendLine($"Server proc: {procLine}");
            report.Text.AppendLine($"Profiler:    {profiler.Describe()}");
            if (concealment != null) report.Text.AppendLine($"Concealment: {concealment.Describe()}");
            report.Text.AppendLine();

            var commands = config.DiagnosticCommands
                .Select(c => c?.Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .ToArray();

            foreach (var command in commands)
            {
                ct.ThrowIfCancellationRequested();

                if (command.StartsWith("!simwatch", StringComparison.OrdinalIgnoreCase))
                {
                    report.Section(command, "(skipped: SimWatch commands can't be used as diagnostics)", "skipped");
                    continue;
                }

                if (profiler.State == ProfilerState.Missing && command.StartsWith("!profile", StringComparison.OrdinalIgnoreCase))
                {
                    report.Section(command, "(skipped: the Profiler plugin is not installed or not enabled)", "skipped");
                    continue;
                }

                Log.Info($"SimWatch: running '{command}'");
                var result = await RunCommandAsync(command, config, sample.Avg, ct);
                var shown = IsLimitedProfile(command) ? LimitResults(result.Text, config.MaxResultLines) : result.Text;
                report.Section(command, shown, StatusName(result.Status), result.Seconds);

                if (config.InspectSmallClusters && IsPhysicsProfile(command))
                {
                    await InspectTopClusterIfSmallAsync(report, result, config, sample.Avg, ct);
                }
            }

            if (concealment != null) await RunConcealAsync(report, config, concealment, sample, wasLow, ct);

            var finalSpeed = Sync.ServerSimulationRatio;
            report.Text.AppendLine($"Sim speed when diagnostics finished: {finalSpeed:0.00}");
            report.Text.AppendLine();

            var estimate = CauseEstimator.Estimate(config, sample, profiler, report.Sections);

            // repeat offender flag: only real low-speed alerts are remembered; the line goes in before the closing note
            var repeats = _history.Analyse(config, CauseEstimator.TopFindings(report.Sections), DateTime.UtcNow, wasLow);
            if (repeats.Count > 0) estimate.InsertRange(estimate.Count - 1, repeats);
            report.Text.AppendLine("===== estimated cause =====");
            var estimateWidth = estimate.Max(e => e.Key.Length);
            foreach (var item in estimate) report.Text.AppendLine($"{item.Key.PadRight(estimateWidth)} : {item.Value}");
            report.Text.AppendLine();
            report.Text.AppendLine(DiscordNotifier.FooterText);

            var reportText = report.Text.ToString();
            var path = WriteReport(reportText);
            LastReportPath = path;
            MarkDiagnosticsFinished();

            Log.Info($"SimWatch: diagnostics finished, report saved to {path}{Environment.NewLine}{reportText}");

            var outcome = new CheckOutcome { DiagnosticsRan = true, ReportPath = path };

            var discord = new DiscordReport
            {
                Kind = manualScan ? "MANUAL SCAN" : wasLow ? "SIM SPEED ALERT" : "TEST RUN",
                Color = sample.Avg < config.SimSpeedThreshold ? (sample.Avg < 0.5f ? 0xE74C3C : 0xE67E22) : 0x00E5FF,
                Closing = $"sim_speed_at_end : {finalSpeed:0.00}\nsaved_report     : {Path.GetFileName(path)}",
                FileName = Path.GetFileName(path),
                FullText = reportText,
            };
            discord.Header.Add(new KeyValuePair<string, string>("host", Environment.MachineName));
            if (!string.IsNullOrEmpty(config.SectorName)) discord.Header.Add(new KeyValuePair<string, string>("sector", config.SectorName));
            discord.Header.Add(new KeyValuePair<string, string>("time", time));
            discord.Header.Add(new KeyValuePair<string, string>("trigger", trigger));
            discord.Header.Add(new KeyValuePair<string, string>("sim_speed", simLine));
            discord.Header.Add(new KeyValuePair<string, string>("process", procLine));
            discord.Header.Add(new KeyValuePair<string, string>("profiler", profiler.Describe()));
            if (concealment != null) discord.Header.Add(new KeyValuePair<string, string>("concealment", concealment.Describe()));
            discord.Header.Add(new KeyValuePair<string, string>("commands", commands.Length.ToString()));
            discord.Estimate.AddRange(estimate);
            discord.Sections.AddRange(report.Sections);

            // an automatic alert whose sim speed is back at 1.00 by the end is only saved, not posted
            var recovered = config.SkipDiscordWhenRecovered && wasLow && !manualScan && finalSpeed >= 0.995f;
            await SendToDiscordAsync(outcome, discord, manualScan, recovered ? $"sim speed back at {finalSpeed:0.00} when the diagnostics finished" : null);
            await TryAnnounceGalaxyAsync(config, wasLow, sample, finalSpeed);
            return outcome;
        }

        /// <summary>
        /// After a real alert: if the register holds a name that hasn't been announced yet, post the whole register to the
        /// Galaxy Offenders webhook. Failures only go to the log.
        /// </summary>
        async Task TryAnnounceGalaxyAsync(SimWatchConfig config, bool recorded, SimSample sample, float finalSpeed)
        {
            if (!recorded || _notifier == null || !_notifier.GalaxyReady) return;

            try
            {
                var now = DateTime.UtcNow;
                var offenders = _history.PendingAnnouncement(config, now, out var hasNew);
                if (!hasNew || offenders.Count == 0) return;

                // grids that reached the GPS level: look up where they are right now (game thread)
                foreach (var offender in offenders.Where(o => o.Category == "grids" && o.Count >= config.GalaxyGpsThreshold))
                {
                    string gps = null;
                    var name = offender.Display;
                    try
                    {
                        await _torch.InvokeAsync(() => { gps = GridLocator.FindGps(name); });
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, $"SimWatch: could not find the position of '{name}'");
                    }

                    offender.Gps = gps ?? "";
                }

                var context = new GalaxyContext
                {
                    SimAvg = sample.Avg,
                    SimMin = sample.Min,
                    FinalSpeed = finalSpeed,
                    AlertsInWindow = _history.CountAlerts(config, now),
                };

                var error = await _notifier.SendGalaxyAsync(offenders, context);
                if (error == null)
                {
                    _history.MarkAnnounced(offenders, config, now);
                    Log.Info($"SimWatch: Galaxy Offenders bulletin sent ({offenders.Count} on the register)");
                }
                else
                {
                    Log.Warn($"SimWatch: Galaxy Offenders bulletin failed: {error}");
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SimWatch: Galaxy Offenders bulletin failed");
            }
        }

        static string TriggerText(bool wasLow, bool manualScan, bool live)
        {
            if (manualScan) return "manual scan (!simwatch scan)";
            if (live) return "sim speed below threshold (live monitor)";
            return wasLow ? "sim speed below threshold" : "forced (test run)";
        }

        /// <summary>Runs the Concealment command (default `!conceal 2000`) after a low-sim-speed diagnostics run.</summary>
        async Task RunConcealAsync(ReportBuilder report, SimWatchConfig config, ProfilerInfo concealment, SimSample sample, bool wasLow, CancellationToken ct)
        {
            var command = config.ConcealCommand;

            if (!wasLow)
            {
                report.Section(command, "(skipped: this run was not triggered by a sim speed below the threshold)", "skipped");
                return;
            }

            if (command.StartsWith("!simwatch", StringComparison.OrdinalIgnoreCase))
            {
                report.Section(command, "(skipped: SimWatch commands can't be used here)", "skipped");
                return;
            }

            if (concealment.State == ProfilerState.Missing)
            {
                report.Section(command, "(skipped: the Concealment plugin is not installed or not enabled)", "skipped");
                return;
            }

            ct.ThrowIfCancellationRequested();
            Log.Info($"SimWatch: running '{command}'");
            var result = await RunCommandAsync(command, config, sample.Avg, ct);
            report.Section(command, result.Text, StatusName(result.Status), result.Seconds);
        }

        // profile results get cut to the top N entries; --inspect / --takeme are not rankings
        static bool IsLimitedProfile(string command)
        {
            return command.StartsWith("!profile", StringComparison.OrdinalIgnoreCase) &&
                   command.IndexOf("--inspect=", StringComparison.OrdinalIgnoreCase) < 0 &&
                   command.IndexOf("--takeme", StringComparison.OrdinalIgnoreCase) < 0;
        }

        static bool IsBanner(string line)
        {
            return line.IndexOf("profiling", StringComparison.OrdinalIgnoreCase) >= 0; // "Started/Finished profiling ..."
        }

        /// <summary>
        /// Keeps the first <paramref name="max"/> ranked lines (lines with a ms or % figure) of a profile result and drops the
        /// rest, together with their indented detail lines. Headings and the "Finished profiling" banner stay.
        /// </summary>
        static string LimitResults(string text, int max)
        {
            if (max <= 0 || string.IsNullOrEmpty(text)) return text;

            var kept = new List<string>();
            var ranked = 0;
            var omitted = 0;
            var dropping = false;

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var isRanked = !IsBanner(line) && ResultLineRegex.IsMatch(line);

                if (isRanked)
                {
                    ranked++;
                    if (ranked > max)
                    {
                        omitted++;
                        dropping = true;
                        continue;
                    }

                    dropping = false;
                    kept.Add(line);
                    continue;
                }

                if (dropping && !IsBanner(line) && line.Length > 0 && char.IsWhiteSpace(line[0])) continue; // detail of a dropped entry

                dropping = false;
                kept.Add(line);
            }

            if (omitted == 0) return text;

            return string.Join(Environment.NewLine, kept).TrimEnd() + Environment.NewLine + $"(top {max} shown, {omitted} more not shown)";
        }

        static string StatusName(WaitResult status)
        {
            switch (status)
            {
                case WaitResult.Completed: return "completed";
                case WaitResult.NoResponse: return "no_response";
                default: return "timed_out";
            }
        }

        /// <summary>
        /// Looks at cluster 0 in the physics result. If it holds fewer entities than the threshold,
        /// runs `!profile physics --inspect=0` to list the grids in it.
        /// </summary>
        async Task InspectTopClusterIfSmallAsync(ReportBuilder report, CommandResult physicsResult, SimWatchConfig config, float simSpeed, CancellationToken ct)
        {
            if (physicsResult.Status != WaitResult.Completed)
            {
                report.Note("(physics profile did not complete; cluster inspection skipped)");
                return;
            }

            var match = TopClusterRegex.Match(physicsResult.Output ?? "");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var entityCount))
            {
                report.Note("(cluster 0 not found in the physics result; inspection skipped)");
                return;
            }

            if (entityCount >= config.InspectEntityThreshold)
            {
                report.Note($"(cluster 0 has {entityCount} entities, not below {config.InspectEntityThreshold}; inspection not needed)");
                return;
            }

            report.Note($"(cluster 0 has only {entityCount} entities, below {config.InspectEntityThreshold}; inspecting it)");

            // The Profiler registers the cluster list for --inspect just after it prints the result.
            await Task.Delay(2000, ct);

            Log.Info($"SimWatch: running '{InspectCommand}'");
            var inspect = await RunCommandAsync(InspectCommand, config, simSpeed, ct);
            report.Section(InspectCommand, inspect.Text, StatusName(inspect.Status), inspect.Seconds);
        }

        static bool IsPhysicsProfile(string command)
        {
            return command.StartsWith("!profile physics", StringComparison.OrdinalIgnoreCase) &&
                   command.IndexOf("--inspect=", StringComparison.OrdinalIgnoreCase) < 0 &&
                   command.IndexOf("--takeme", StringComparison.OrdinalIgnoreCase) < 0;
        }

        async Task SendToDiscordAsync(CheckOutcome outcome, DiscordReport discord, bool manualScan, string skipReason)
        {
            if (skipReason != null)
            {
                outcome.DiscordSkipped = skipReason;
                Log.Info($"SimWatch: report not sent to Discord ({skipReason}); it is saved in the report folder");
                return;
            }

            // `!simwatch scan` always sends; automatic runs only when the Discord option is enabled.
            if (_notifier == null) return;
            if (!manualScan && !_notifier.IsEnabled) return;
            if (!_notifier.HasWebhook)
            {
                outcome.DiscordError = "no webhook URL set";
                Log.Warn("SimWatch: not sending to Discord, no webhook URL set");
                return;
            }

            var error = await _notifier.SendReportAsync(discord);
            outcome.DiscordSent = error == null;
            outcome.DiscordError = error;

            if (error == null) Log.Info("SimWatch: report sent to Discord");
            else Log.Warn($"SimWatch: sending the report to Discord failed: {error}");
        }

        async Task<CommandResult> RunCommandAsync(string command, SimWatchConfig config, float simSpeed, CancellationToken ct)
        {
            var capture = new CommandCapture(config.CompletionMarker);
            var timer = Stopwatch.StartNew();

            // Profile commands announce completion with the marker text; other commands
            // (including --inspect, which just lists grids) are done once their output goes quiet.
            var awaitsMarker = command.StartsWith("!profile", StringComparison.OrdinalIgnoreCase) &&
                               command.IndexOf("--inspect=", StringComparison.OrdinalIgnoreCase) < 0 &&
                               command.IndexOf("--takeme", StringComparison.OrdinalIgnoreCase) < 0;

            try
            {
                // Run on the game thread, like a command typed into the Torch console.
                await _torch.InvokeAsync(() =>
                {
                    var commandManager = _torch.CurrentSession?.Managers.GetManager<CommandManager>();
                    if (commandManager == null)
                    {
                        capture.Add("(command manager not available)");
                        return;
                    }

                    commandManager.HandleCommandFromServer(command, message => capture.Add(message));
                });

                var timeout = TimeSpan.FromSeconds(ExpectedSeconds(command, awaitsMarker, simSpeed) + config.CommandGraceSeconds);
                var status = await capture.WaitAsync(awaitsMarker, timeout, ct);
                var output = capture.GetText();

                string text;
                switch (status)
                {
                    case WaitResult.NoResponse:
                        text = output + "(no response: command not found, disabled, or it printed nothing)";
                        break;
                    case WaitResult.TimedOut:
                        text = output + Environment.NewLine + "(timed out waiting for the command to finish)";
                        break;
                    default:
                        text = output;
                        break;
                }

                return new CommandResult { Output = output, Text = text, Status = status, Seconds = timer.Elapsed.TotalSeconds };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Error(e, $"SimWatch: command '{command}' failed");
                var output = capture.GetText();
                return new CommandResult
                {
                    Output = output,
                    Text = output + Environment.NewLine + $"(error: {e.Message})",
                    Status = WaitResult.TimedOut,
                    Seconds = timer.Elapsed.TotalSeconds,
                };
            }
        }

        static double ExpectedSeconds(string command, bool awaitsMarker, float simSpeed)
        {
            if (!awaitsMarker) return 0;

            var tics = TicsRegex.Match(command);
            if (tics.Success && int.TryParse(tics.Groups[1].Value, out var ticCount))
            {
                // physics profiling counts game frames: 60/s at full speed, fewer when the server is lagging
                // (which is exactly when this runs), plus some margin for the profiling overhead
                return ticCount / (60.0 * Math.Max(0.1, simSpeed)) * 1.5;
            }

            var secs = SecsRegex.Match(command);
            if (secs.Success && int.TryParse(secs.Groups[1].Value, out var secCount)) return secCount;

            return DefaultProfileSeconds;
        }

        string WriteReport(string text)
        {
            Directory.CreateDirectory(_reportDir);
            var path = Path.Combine(_reportDir, $"simwatch-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text, Encoding.UTF8);
            CleanupReports();
            return path;
        }

        /// <summary>Deletes saved reports beyond the "keep this many" count and those older than the retention days.</summary>
        public void CleanupReports()
        {
            try
            {
                if (!Directory.Exists(_reportDir)) return;

                var config = _getConfig();
                var files = new DirectoryInfo(_reportDir)
                    .GetFiles("simwatch-*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                var remove = new List<FileInfo>(files.Skip(config.MaxReports));
                if (config.ReportRetentionDays > 0)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-config.ReportRetentionDays);
                    remove.AddRange(files.Where(f => f.LastWriteTimeUtc < cutoff));
                }

                foreach (var file in remove.Distinct())
                {
                    file.Delete();
                    Log.Info($"SimWatch: deleted old report {file.Name}");
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SimWatch: failed to clean up old reports");
            }
        }
    }
}
