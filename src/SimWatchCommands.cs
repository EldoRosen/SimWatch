using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace SimWatch
{
    [Category("simwatch")]
    public sealed class SimWatchCommands : CommandModule
    {
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        SimWatchPlugin Owner => (SimWatchPlugin)Context.Plugin;

        [Command("status", "Shows SimWatch settings and the result of the last check")]
        [Permission(MyPromoteLevel.Admin)]
        public void Status()
        {
            var config = Owner.Config;
            var watcher = Owner.Watcher;

            var msg = new StringBuilder();
            var timer = config.CheckIntervalMinutes > 0 ? $"every {config.CheckIntervalMinutes} min" : "off";
            var live = config.LiveMonitorEnabled ? $"on (cooldown {config.LiveCooldownMinutes} min)" : "off";
            msg.AppendLine($"Enabled: {config.Enabled}; timer: {timer}; live monitor: {live}; sample {config.SampleSeconds}s; threshold {config.SimSpeedThreshold:0.00}");
            if (watcher.LiveAverage is float liveAvg) msg.AppendLine($"Live average right now: {liveAvg:0.00}");
            msg.AppendLine($"Profiler plugin: {watcher.GetProfilerInfo().Describe()}");
            msg.AppendLine($"Concealment plugin: {watcher.GetConcealmentInfo().Describe()}; run '{config.ConcealCommand}' after diagnostics: {(config.ConcealAfterDiagnostics ? "on" : "off")}");
            msg.AppendLine($"Diagnostic commands: {config.DiagnosticCommands.Count}");

            if (watcher.LastCheckUtc is DateTime last && watcher.LastSample is SimSample s)
            {
                var ago = DateTime.UtcNow - last;
                msg.AppendLine($"Last check {ago.TotalMinutes:0} min ago: avg {s.Avg:0.00}, min {s.Min:0.00}, max {s.Max:0.00}");
            }
            else
            {
                msg.AppendLine("No check has run yet.");
            }

            msg.AppendLine($"Last report: {watcher.LastReportPath ?? "none"}");
            msg.Append($"Reports folder: {watcher.ReportDirectory}");
            if (watcher.IsBusy) msg.Append("\n(a check is running right now)");

            Context.Respond(msg.ToString());
        }

        [Command("profiler", "Checks whether the Profiler and Concealment plugins are installed")]
        [Permission(MyPromoteLevel.Admin)]
        public void ProfilerCheck()
        {
            var info = Owner.Watcher.GetProfilerInfo();
            var conceal = Owner.Watcher.GetConcealmentInfo();
            var msg = $"Profiler plugin: {info.Describe()}\nConcealment plugin: {conceal.Describe()}";
            if ((info.State == ProfilerState.Missing || conceal.State == ProfilerState.Missing) && info.Seen.Count > 0) msg += $"\nPlugins seen: {string.Join(", ", info.Seen)}";
            Context.Respond(msg);
        }

        [Command("offenders", "Shows the Galaxy Offenders register (names that were the top result in several alerts)")]
        [Permission(MyPromoteLevel.Admin)]
        public void Offenders()
        {
            var config = Owner.Config;
            var list = Owner.Watcher.GetOffenders();
            var msg = new StringBuilder();
            msg.AppendLine($"Galaxy Offenders: {(config.GalaxyEnabled ? "on" : "off")}; on the register at {config.GalaxyThreshold}+ alerts within {config.RepeatWindowDays} days");

            if (list.Count == 0) msg.Append("The register is empty.");
            foreach (var o in list)
            {
                msg.AppendLine($"{o.Count}x {o.Category}: {o.Display}");
            }

            Context.Respond(msg.ToString().TrimEnd());
        }

        [Command("share", "Cross-server config sharing: !simwatch share status | push | pull")]
        [Permission(MyPromoteLevel.Admin)]
        public void Share()
        {
            var action = Context.Args.Count > 0 ? Context.Args[0].ToLowerInvariant() : "status";

            switch (action)
            {
                case "push":
                    Context.Respond(Owner.PublishNow());
                    break;
                case "pull":
                    Context.Respond(Owner.PullNow());
                    break;
                default:
                    Context.Respond(Owner.DescribeShare());
                    break;
            }
        }

        [Command("check", "Samples the sim speed now; runs the diagnostics only if it is below the threshold")]
        [Permission(MyPromoteLevel.Admin)]
        public void Check() => RunCheck(force: false, scan: false);

        [Command("test", "Runs the full diagnostic sequence now, even if the sim speed is fine")]
        [Permission(MyPromoteLevel.Admin)]
        public void Test() => RunCheck(force: true, scan: false);

        [Command("scan", "Runs the full diagnostic sequence now and sends the whole report to Discord")]
        [Permission(MyPromoteLevel.Admin)]
        public void Scan() => RunCheck(force: true, scan: true);

        [Command("reload", "Reloads SimWatch.cfg")]
        [Permission(MyPromoteLevel.Admin)]
        public void Reload()
        {
            Owner.ReloadConfig();
            Context.Respond("SimWatch.cfg reloaded");
        }

        void RunCheck(bool force, bool scan)
        {
            var watcher = Owner.Watcher;
            if (watcher.IsBusy)
            {
                Context.Respond("A check is already running; see !simwatch status");
                return;
            }

            if (scan && !Owner.Notifier.HasWebhook)
            {
                Context.Respond("No Discord webhook URL is set. Add one in the SimWatch settings (Torch UI > Plugins > SimWatch) first.");
                return;
            }

            var seconds = Owner.Config.SampleSeconds;
            var count = Owner.Config.DiagnosticCommands.Count;
            if (scan) Context.Respond($"Scan started: sampling sim speed for {seconds}s, then running {count} diagnostic commands. The report goes to Discord when done; this takes a few minutes.");
            else if (force) Context.Respond($"Forced run: sampling sim speed for {seconds}s, then running {count} diagnostic commands. This takes a few minutes.");
            else Context.Respond($"Sampling sim speed for {seconds}s...");

            Task.Run(async () =>
            {
                try
                {
                    var outcome = await watcher.CheckAsync(force, CancellationToken.None, scan);
                    var s = outcome.Sample;
                    var line = $"Sim speed avg {s.Avg:0.00}, min {s.Min:0.00}, max {s.Max:0.00}";

                    if (!outcome.DiagnosticsRan)
                    {
                        Context.Respond($"{line}. Above the threshold, no diagnostics needed.");
                    }
                    else if (scan)
                    {
                        Context.Respond(outcome.DiscordSent
                            ? $"{line}. Scan finished and sent to Discord. Report: {outcome.ReportPath}"
                            : $"{line}. Scan finished but the Discord send failed ({outcome.DiscordError}). Report: {outcome.ReportPath}");
                    }
                    else
                    {
                        Context.Respond($"{line}. Diagnostics finished; report: {outcome.ReportPath}");
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "SimWatch check via command failed");
                    Context.Respond($"Check failed: {e.Message}");
                }
            });
        }
    }
}
