using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using NLog;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;

namespace SimWatch
{
    /// <summary>
    /// Checks the server sim speed (on an optional timer and/or continuously) and, when it drops below the threshold, runs diagnostic commands
    /// (by default the Profiler plugin's `!profile ...` commands), saves a report and optionally posts it to Discord.
    /// </summary>
    public sealed class SimWatchPlugin : TorchPluginBase, IWpfPlugin
    {
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        string _configPath;
        CancellationTokenSource _canceller;
        TorchSessionManager _sessions;
        SimWatchControl _control;

        Timer _shareTimer;
        readonly object _shareLock = new object();
        string _shareSignature;
        bool _shareDirty;
        DateTime _shareDirtyUtc;

        public SimWatchConfig Config { get; private set; }
        public SimWatcher Watcher { get; private set; }
        public DiscordNotifier Notifier { get; private set; }
        public string ConfigPath => _configPath;
        public volatile bool IsSessionRunning;
        public string ShareStatus { get; private set; } = "Sharing is off.";

        UserControl IWpfPlugin.GetControl()
        {
            return _control ??= new SimWatchControl(this);
        }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);

            _configPath = Path.Combine(StoragePath, "SimWatch.cfg");
            ReloadConfig();

            Notifier = new DiscordNotifier(() => Config);
            Watcher = new SimWatcher(torch, () => Config, Path.Combine(StoragePath, "SimWatch-Reports"), Notifier);

            ShareTick(true);
            _shareTimer = new Timer(_ => ShareTick(false), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

            _sessions = torch.Managers.GetManager<TorchSessionManager>();
            if (_sessions != null)
            {
                _sessions.SessionStateChanged += OnSessionStateChanged;
            }
            else
            {
                Log.Error("SimWatch: session manager not found; the periodic check will not start");
            }
        }

        /// <summary>(Re)reads SimWatch.cfg. Changes made later through the GUI are saved automatically.</summary>
        public void ReloadConfig()
        {
            if (Config != null) Config.PropertyChanged -= OnConfigChanged;

            Config = SimWatchConfig.Load(_configPath);
            Config.PropertyChanged += OnConfigChanged;
            _shareSignature = null;
            _shareDirty = true;
            _shareDirtyUtc = DateTime.UtcNow;

            var timer = Config.CheckIntervalMinutes > 0 ? $"timer every {Config.CheckIntervalMinutes} min" : "no timer";
            var live = Config.LiveMonitorEnabled ? "live monitor on" : "live monitor off";
            Log.Info($"SimWatch: config loaded ({Config.DiagnosticCommands.Count} diagnostic commands, {timer}, {live}, threshold {Config.SimSpeedThreshold:0.00})");
            _control?.OnConfigReloaded();
        }

        void OnConfigChanged(object sender, PropertyChangedEventArgs e)
        {
            Config.Save(_configPath);

            var name = e.PropertyName;
            if (name == nameof(SimWatchConfig.SharedConfigMode) || name == nameof(SimWatchConfig.SharedConfigPath) || name == nameof(SimWatchConfig.SharedIncludeDiscord))
            {
                _shareSignature = null;
                _shareDirty = true;
                _shareDirtyUtc = DateTime.UtcNow;
            }
            else if (Config.SharedConfigMode == "Publish" && name != null && SharedConfig.IsShared(Config, name))
            {
                _shareDirty = true;
                _shareDirtyUtc = DateTime.UtcNow;
            }
        }

        // ---- cross-server config sharing ----

        public string DescribeShare()
        {
            var path = SharedConfig.ResolvePath(Config.SharedConfigPath);
            return $"Cross-server sharing: {Config.SharedConfigMode}; file: {path ?? "(no path set)"}; Discord settings shared: {(Config.SharedIncludeDiscord ? "yes" : "no")}\n{ShareStatus}";
        }

        /// <summary>Writes this server's shared settings to the shared file, whatever the mode.</summary>
        public string PublishNow()
        {
            lock (_shareLock)
            {
                return DoPublish();
            }
        }

        /// <summary>Applies the shared file to this server, whatever the mode.</summary>
        public string PullNow()
        {
            lock (_shareLock)
            {
                return DoPull(true);
            }
        }

        string DoPublish()
        {
            var path = SharedConfig.Publish(Config, out var error);
            _shareDirty = false;

            if (path == null)
            {
                ShareStatus = "Publish failed: " + error;
                Log.Warn("SimWatch: " + ShareStatus);
            }
            else
            {
                _shareSignature = SharedConfig.Signature(path);
                ShareStatus = $"Published to {path} at {DateTime.Now:HH:mm:ss}";
                Log.Info("SimWatch: " + ShareStatus);
            }

            return ShareStatus;
        }

        string DoPull(bool force)
        {
            var path = SharedConfig.ResolvePath(Config.SharedConfigPath);
            if (path == null)
            {
                ShareStatus = "Pull failed: no shared path set";
                return ShareStatus;
            }

            if (!File.Exists(path))
            {
                ShareStatus = $"Shared file not found: {path}";
                return ShareStatus;
            }

            var signature = SharedConfig.Signature(path);
            if (!force && signature == _shareSignature) return ShareStatus;

            var result = SharedConfig.Pull(Config, path);
            if (!result.Ok)
            {
                ShareStatus = "Pull failed: " + result.Error;
                Log.Warn("SimWatch: " + ShareStatus);
                if (!result.Retry) _shareSignature = signature; // unreadable content: retry only when the file changes, not every 15 s
                return ShareStatus;
            }

            _shareSignature = signature;

            if (result.Changed == 0)
            {
                ShareStatus = $"Up to date with {path} (published by {result.PublishedBy}, {result.PublishedUtc})";
            }
            else
            {
                ShareStatus = $"Applied {result.Changed} changed setting(s) from {path} (published by {result.PublishedBy}, {result.PublishedUtc}): {string.Join(", ", result.ChangedNames)}";
                Log.Info("SimWatch: " + ShareStatus);
            }

            return ShareStatus;
        }

        void ShareTick(bool force)
        {
            try
            {
                lock (_shareLock)
                {
                    switch (Config.SharedConfigMode)
                    {
                        case "Follow":
                            DoPull(force || _shareSignature == null);
                            break;

                        case "Publish":
                            var path = SharedConfig.ResolvePath(Config.SharedConfigPath);
                            var due = _shareDirty && DateTime.UtcNow - _shareDirtyUtc >= TimeSpan.FromSeconds(5);
                            if (force || due || (path != null && !File.Exists(path))) DoPublish();
                            break;

                        default:
                            ShareStatus = "Sharing is off.";
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SimWatch: shared config sync failed");
            }
        }

        void OnSessionStateChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loaded:
                    IsSessionRunning = true;
                    Start();
                    break;
                case TorchSessionState.Unloading:
                    IsSessionRunning = false;
                    Stop();
                    break;
            }
        }

        void Start()
        {
            Stop();
            _canceller = new CancellationTokenSource();
            var token = _canceller.Token;
            Task.Run(() => Watcher.RunLoopAsync(token));
        }

        void Stop()
        {
            // Cancelled but not disposed on purpose: running tasks may still hold the token. The source holds no unmanaged resources.
            _canceller?.Cancel();
            _canceller = null;
        }

        /// <summary>Cancelled when the session unloads; manual runs use it so they stop with the session. Already cancelled when no session runs.</summary>
        public CancellationToken SessionToken
        {
            get
            {
                var source = _canceller;
                return source != null ? source.Token : new CancellationToken(true);
            }
        }

        public override void Dispose()
        {
            Stop();
            _shareTimer?.Dispose();
            if (_sessions != null) _sessions.SessionStateChanged -= OnSessionStateChanged;
            if (Config != null) Config.PropertyChanged -= OnConfigChanged;
            base.Dispose();
        }
    }
}
