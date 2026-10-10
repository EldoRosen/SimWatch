using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Serialization;
using NLog;

namespace SimWatch
{
    /// <summary>
    /// Stored as SimWatch.cfg in the Torch instance folder. Edited from the plugin's GUI (saved automatically)
    /// or by hand (then use "Reload from file" / `!simwatch reload`).
    /// INotifyPropertyChanged is implemented manually so the WPF GUI can bind to it.
    /// </summary>
    public sealed class SimWatchConfig : INotifyPropertyChanged
    {
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        public const string DefaultCompletionMarker = "Finished profiling";
        public const string DefaultConcealCommand = "!conceal 2000";

        // ---- INotifyPropertyChanged ----

        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        // Like SetField, but clamps; if the clamp changed what the user typed, notify anyway
        // so a bound text box shows the corrected value instead of the rejected one.
        bool SetClamped(ref int field, int value, int min, int max, [CallerMemberName] string propertyName = null)
        {
            var clamped = Math.Min(max, Math.Max(min, value));
            if (SetField(ref field, clamped, propertyName)) return true;
            if (clamped != value) OnPropertyChanged(propertyName);
            return false;
        }

        // ---- fields ----

        bool _enabled = true;
        int _checkIntervalMinutes = 30;
        bool _liveMonitorEnabled;
        int _liveCooldownMinutes = 30;
        int _sampleSeconds = 10;
        float _simSpeedThreshold = 0.9f;
        int _commandGraceSeconds = 30;
        int _maxReports = 50;
        int _reportRetentionDays = 2;
        int _maxResultLines = 10;
        string _sectorName = "";
        int _repeatLookbackAlerts = 5;
        int _repeatWindowDays = 7;
        bool _concealAfterDiagnostics;
        string _concealCommand = DefaultConcealCommand;
        string _completionMarker = DefaultCompletionMarker;
        List<string> _diagnosticCommands = new List<string>();
        bool _inspectSmallClusters = true;
        int _inspectEntityThreshold = 20;
        bool _discordEnabled;
        string _discordWebhookUrl = "";
        string _discordUsername = "SimWatch";
        string _discordPingRoleId = "";
        string _sharedConfigMode = "Off";
        string _sharedConfigPath = "";
        bool _sharedIncludeDiscord;
        bool _skipDiscordWhenRecovered = true;
        bool _galaxyEnabled;
        string _galaxyWebhookUrl = "";
        string _galaxyUsername = "Galaxy Offenders";
        int _galaxyThreshold = 3;
        int _galaxyGpsThreshold = 10;
        bool _discordAttachFile;

        // ---- check settings ----

        /// <summary>Master switch for the automatic checks (timer and live monitor).</summary>
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        /// <summary>
        /// Time between timed checks. 0 = no timer (optional): use the live monitor, or run checks by command.
        /// </summary>
        public int CheckIntervalMinutes
        {
            get => _checkIntervalMinutes;
            set => SetClamped(ref _checkIntervalMinutes, value, 0, 100000);
        }

        /// <summary>
        /// Watch the sim speed continuously (one reading per second). As soon as the average over
        /// <see cref="SampleSeconds"/> is below the threshold, the diagnostics run. Needs no timer.
        /// </summary>
        public bool LiveMonitorEnabled
        {
            get => _liveMonitorEnabled;
            set => SetField(ref _liveMonitorEnabled, value);
        }

        /// <summary>Live monitor only: minimum time between the end of one diagnostics run and the next automatic one.</summary>
        public int LiveCooldownMinutes
        {
            get => _liveCooldownMinutes;
            set => SetClamped(ref _liveCooldownMinutes, value, 1, 100000);
        }

        /// <summary>
        /// Each check averages the sim speed over this many seconds (one reading per second),
        /// so a single bad frame doesn't trigger the diagnostics.
        /// </summary>
        public int SampleSeconds
        {
            get => _sampleSeconds;
            set => SetClamped(ref _sampleSeconds, value, 1, 300);
        }

        /// <summary>Diagnostics run when the averaged sim speed is below this value (1.0 = full speed).</summary>
        public float SimSpeedThreshold
        {
            get => _simSpeedThreshold;
            set
            {
                var clamped = Math.Min(2f, Math.Max(0.01f, value));
                if (!SetField(ref _simSpeedThreshold, clamped) && clamped != value) OnPropertyChanged();
            }
        }

        /// <summary>Extra seconds to wait for a command to finish, on top of its own duration.</summary>
        public int CommandGraceSeconds
        {
            get => _commandGraceSeconds;
            set => SetClamped(ref _commandGraceSeconds, value, 5, 100000);
        }

        /// <summary>Oldest reports beyond this count are deleted.</summary>
        public int MaxReports
        {
            get => _maxReports;
            set => SetClamped(ref _maxReports, value, 1, 100000);
        }

        /// <summary>
        /// Each `!profile` result (grids, blocktypes, scripts, ...) keeps only its top N entries in the report and the
        /// Discord post. 0 = keep everything.
        /// </summary>
        public int MaxResultLines
        {
            get => _maxResultLines;
            set => SetClamped(ref _maxResultLines, value, 0, 1000);
        }

        /// <summary>
        /// Repeat offender flag: compare this alert with the latest N alerts (this one included) and say when the same grid,
        /// script or player was the top result of a profile command in 2 or more of them. 0 = off.
        /// </summary>
        public int RepeatLookbackAlerts
        {
            get => _repeatLookbackAlerts;
            set => SetClamped(ref _repeatLookbackAlerts, value, 0, 50);
        }

        /// <summary>Repeat offender flag: only alerts from the last N days are compared.</summary>
        public int RepeatWindowDays
        {
            get => _repeatWindowDays;
            set => SetClamped(ref _repeatWindowDays, value, 1, 90);
        }

        /// <summary>Optional name of the sector/server, shown in the saved report and the Discord post. Empty = not shown.</summary>
        public string SectorName
        {
            get => _sectorName;
            set
            {
                var name = (value ?? "").Trim();
                if (name.Length > 100) name = name.Substring(0, 100);
                if (!SetField(ref _sectorName, name) && name != value) OnPropertyChanged();
            }
        }

        /// <summary>Saved reports older than this many days are deleted (checked hourly and after each report). 0 = keep by count only.</summary>
        public int ReportRetentionDays
        {
            get => _reportRetentionDays;
            set => SetClamped(ref _reportRetentionDays, value, 0, 3650);
        }

        /// <summary>
        /// Needs the Concealment plugin. After a diagnostics run that was triggered by a low sim speed, also run
        /// <see cref="ConcealCommand"/> and put its output in the report.
        /// </summary>
        public bool ConcealAfterDiagnostics
        {
            get => _concealAfterDiagnostics;
            set => SetField(ref _concealAfterDiagnostics, value);
        }

        /// <summary>The Concealment command run after the diagnostics (include the "!" prefix).</summary>
        public string ConcealCommand
        {
            get => _concealCommand;
            set
            {
                var command = string.IsNullOrWhiteSpace(value) ? DefaultConcealCommand : value.Trim();
                if (!SetField(ref _concealCommand, command) && command != value) OnPropertyChanged();
            }
        }

        /// <summary>Text in a command's output that means "this profile command is done".</summary>
        public string CompletionMarker
        {
            get => _completionMarker;
            set
            {
                var marker = string.IsNullOrWhiteSpace(value) ? DefaultCompletionMarker : value.Trim();
                if (!SetField(ref _completionMarker, marker) && marker != value) OnPropertyChanged();
            }
        }

        // ---- commands ----

        /// <summary>Commands run in order when sim speed is low. Include the command prefix (usually "!").</summary>
        [XmlArray("DiagnosticCommands")]
        [XmlArrayItem("Command")]
        public List<string> DiagnosticCommands
        {
            get => _diagnosticCommands;
            set
            {
                _diagnosticCommands = value ?? new List<string>();
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiagnosticCommandsText));
            }
        }

        /// <summary>The same list as one command per line; this is what the GUI text box edits.</summary>
        [XmlIgnore]
        public string DiagnosticCommandsText
        {
            get => string.Join(Environment.NewLine, _diagnosticCommands);
            set
            {
                var lines = (value ?? "")
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();

                if (lines.SequenceEqual(_diagnosticCommands)) return;
                _diagnosticCommands = lines;
                OnPropertyChanged();                           // keeps the text box in sync
                OnPropertyChanged(nameof(DiagnosticCommands)); // lets the config sharing see the change
            }
        }

        /// <summary>
        /// After a `!profile physics` command: if the top cluster (index 0) holds fewer entities
        /// than <see cref="InspectEntityThreshold"/>, also run `!profile physics --inspect=0`.
        /// </summary>
        public bool InspectSmallClusters
        {
            get => _inspectSmallClusters;
            set => SetField(ref _inspectSmallClusters, value);
        }

        public int InspectEntityThreshold
        {
            get => _inspectEntityThreshold;
            set => SetClamped(ref _inspectEntityThreshold, value, 1, 100000);
        }

        // ---- Discord ----

        public bool DiscordEnabled
        {
            get => _discordEnabled;
            set => SetField(ref _discordEnabled, value);
        }

        /// <summary>Channel webhook URL. Anyone with this URL can post to the channel; keep it private.</summary>
        public string DiscordWebhookUrl
        {
            get => _discordWebhookUrl;
            set => SetField(ref _discordWebhookUrl, value?.Trim() ?? "");
        }

        /// <summary>Also attach the complete report as a .txt file (the report is always shown in the messages).</summary>
        public bool DiscordAttachFile
        {
            get => _discordAttachFile;
            set => SetField(ref _discordAttachFile, value);
        }

        public string DiscordUsername
        {
            get => _discordUsername;
            set => SetField(ref _discordUsername, value?.Trim() ?? "");
        }

        /// <summary>
        /// Discord role to ping when a sim speed alert is posted (the role's numeric ID; a pasted "&lt;@&amp;123...&gt;" works too).
        /// Empty = no ping. Manual scans and test runs don't ping; the webhook test message does, to prove the ID works.
        /// </summary>
        public string DiscordPingRoleId
        {
            get => _discordPingRoleId;
            set
            {
                var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
                if (digits.Length < 17 || digits.Length > 20) digits = ""; // not a Discord ID
                if (!SetField(ref _discordPingRoleId, digits) && digits != value) OnPropertyChanged();
            }
        }

        /// <summary>
        /// Automatic alert reports are only posted to Discord when the sim speed at the end of the diagnostics is still below
        /// 1.00. If it is back to 1.00 the report is only saved (and logged). Manual scans always post.
        /// </summary>
        public bool SkipDiscordWhenRecovered
        {
            get => _skipDiscordWhenRecovered;
            set => SetField(ref _skipDiscordWhenRecovered, value);
        }

        // ---- cross-server config sharing (these three are never shared themselves) ----

        static string NormalizeShareMode(string value)
        {
            if (string.Equals(value?.Trim(), "Follow", StringComparison.OrdinalIgnoreCase)) return "Follow";
            if (string.Equals(value?.Trim(), "Publish", StringComparison.OrdinalIgnoreCase)) return "Publish";
            return "Off";
        }

        /// <summary>Off, Follow (take the shared settings from the file) or Publish (write this server's settings to the file).</summary>
        public string SharedConfigMode
        {
            get => _sharedConfigMode;
            set
            {
                var mode = NormalizeShareMode(value);
                if (!SetField(ref _sharedConfigMode, mode) && mode != value) OnPropertyChanged();
            }
        }

        /// <summary>Folder or file that all servers can reach (network share, synced folder). A folder gets the file name SimWatch.shared.cfg.</summary>
        public string SharedConfigPath
        {
            get => _sharedConfigPath;
            set => SetField(ref _sharedConfigPath, value?.Trim() ?? "");
        }

        /// <summary>Also share the webhook URLs and the ping role. Must be on on the publishing server and on the followers.</summary>
        public bool SharedIncludeDiscord
        {
            get => _sharedIncludeDiscord;
            set => SetField(ref _sharedIncludeDiscord, value);
        }

        // ---- Galaxy Offenders (second webhook) ----

        /// <summary>Post the "Galaxy Offenders" bulletin to its own webhook when a name reaches the threshold.</summary>
        public bool GalaxyEnabled
        {
            get => _galaxyEnabled;
            set => SetField(ref _galaxyEnabled, value);
        }

        /// <summary>Webhook URL of the channel for the Galaxy Offenders bulletin. Keep it private.</summary>
        public string GalaxyWebhookUrl
        {
            get => _galaxyWebhookUrl;
            set => SetField(ref _galaxyWebhookUrl, value?.Trim() ?? "");
        }

        public string GalaxyUsername
        {
            get => _galaxyUsername;
            set => SetField(ref _galaxyUsername, string.IsNullOrWhiteSpace(value) ? "Galaxy Offenders" : value.Trim());
        }

        /// <summary>
        /// A grid, script or player goes on the register when it was the top result in this many low-sim-speed alerts
        /// within the repeat-offender window (days).
        /// </summary>
        public int GalaxyThreshold
        {
            get => _galaxyThreshold;
            set => SetClamped(ref _galaxyThreshold, value, 2, 50);
        }

        /// <summary>A grid on the register that reached this many incidents also gets its GPS position in the bulletin.</summary>
        public int GalaxyGpsThreshold
        {
            get => _galaxyGpsThreshold;
            set => SetClamped(ref _galaxyGpsThreshold, value, 2, 1000);
        }

        // ---- load / save ----

        public static SimWatchConfig CreateDefault()
        {
            var config = new SimWatchConfig();
            config._diagnosticCommands = new List<string>
            {
                "!profile frames --secs=15",      // overall split: network / replication / session / GPS / idle
                "!profile session --secs=15",     // session components
                "!profile entities --secs=15",    // entity types
                "!profile grids --secs=15 --top=10",
                "!profile blocktypes --secs=15",
                "!profile scripts --secs=15",     // programmable blocks
                "!profile players --secs=15",
                "!profile physics --tics=600",    // physics clusters; small top cluster is auto-inspected
            };
            return config;
        }

        public static SimWatchConfig Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    var serializer = new XmlSerializer(typeof(SimWatchConfig));
                    using (var stream = File.OpenRead(path))
                    {
                        return (SimWatchConfig)serializer.Deserialize(stream);
                    }
                }
                catch (Exception e)
                {
                    // the GUI saves on every edit, which would overwrite this file, so keep a copy
                    try { File.Copy(path, path + ".broken", true); } catch { /* best effort */ }
                    Log.Error(e, $"Failed to read {path}; using default settings. The unreadable file was copied to {path}.broken");
                    return CreateDefault();
                }
            }

            var defaults = CreateDefault();
            defaults.Save(path);
            return defaults;
        }

        readonly object _saveLock = new object();

        public void Save(string path)
        {
            try
            {
                lock (_saveLock)
                {
                    var serializer = new XmlSerializer(typeof(SimWatchConfig));
                    var settings = new XmlWriterSettings { Indent = true };
                    using (var writer = XmlWriter.Create(path, settings))
                    {
                        serializer.Serialize(writer, this);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"Failed to write {path}");
            }
        }
    }
}
