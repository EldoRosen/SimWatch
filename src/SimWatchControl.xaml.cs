using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SimWatch
{
    public partial class SimWatchControl : UserControl
    {
        readonly SimWatchPlugin _plugin;

        public SimWatchControl(SimWatchPlugin plugin)
        {
            _plugin = plugin;
            InitializeComponent();
            DataContext = plugin.Config;
            FooterText.Text = DiscordNotifier.FooterText;
            RefreshProfilerStatus();
            ShareStatusText.Text = _plugin.ShareStatus;
        }

        /// <summary>Shows whether the Profiler plugin was found (green = installed, red = missing, grey = unknown).</summary>
        void RefreshProfilerStatus()
        {
            var info = _plugin.Watcher?.GetProfilerInfo();
            if (info == null)
            {
                ProfilerStatusText.Text = "Profiler plugin: not checked yet";
                ProfilerStatusText.Foreground = Brushes.Gray;
                ConcealStatusText.Text = "Concealment plugin: not checked yet";
                ConcealStatusText.Foreground = Brushes.Gray;
                return;
            }

            ProfilerStatusText.Text = "Profiler plugin: " + info.Describe();
            ProfilerStatusText.Foreground = info.State == ProfilerState.Installed ? Brushes.ForestGreen
                : info.State == ProfilerState.Missing ? Brushes.Firebrick
                : Brushes.Gray;
            ProfilerStatusText.ToolTip = info.Seen.Count > 0 ? "Plugins seen: " + string.Join(", ", info.Seen) : null;

            var conceal = _plugin.Watcher.GetConcealmentInfo();
            ConcealStatusText.Text = "Concealment plugin: " + conceal.Describe();
            ConcealStatusText.Foreground = conceal.State == ProfilerState.Installed ? Brushes.ForestGreen
                : conceal.State == ProfilerState.Missing ? Brushes.Firebrick
                : Brushes.Gray;
            ConcealCheckBox.IsEnabled = conceal.State != ProfilerState.Missing; // the option only makes sense with the plugin
        }

        void OnRecheckProfilerClick(object sender, RoutedEventArgs e)
        {
            RefreshProfilerStatus();
            SetStatus(ProfilerStatusText.Text + "; " + ConcealStatusText.Text);
        }

        /// <summary>Called by the plugin when SimWatch.cfg was re-read; rebinds to the new config object.</summary>
        public void OnConfigReloaded()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                DataContext = _plugin.Config;
                ShareStatusText.Text = _plugin.ShareStatus;
            }));
        }

        void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        async void OnTestWebhookClick(object sender, RoutedEventArgs e)
        {
            TestWebhookButton.IsEnabled = false;
            SetStatus("Sending test message...");
            try
            {
                SetStatus(await _plugin.Notifier.SendTestAsync(_plugin.Watcher?.GetProfilerInfo().Describe()));
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}");
            }
            finally
            {
                TestWebhookButton.IsEnabled = true;
            }
        }

        async void OnSharePublishClick(object sender, RoutedEventArgs e)
        {
            ShareWriteButton.IsEnabled = false;
            try
            {
                var text = await Task.Run(() => _plugin.PublishNow());
                ShareStatusText.Text = text;
                SetStatus(text);
            }
            finally
            {
                ShareWriteButton.IsEnabled = true;
            }
        }

        async void OnSharePullClick(object sender, RoutedEventArgs e)
        {
            ShareReadButton.IsEnabled = false;
            try
            {
                var text = await Task.Run(() => _plugin.PullNow());
                ShareStatusText.Text = text;
                SetStatus(text);
            }
            finally
            {
                ShareReadButton.IsEnabled = true;
            }
        }

        async void OnGalaxyTestClick(object sender, RoutedEventArgs e)
        {
            GalaxyTestButton.IsEnabled = false;
            SetStatus("Sending test bulletin...");
            try
            {
                SetStatus(await _plugin.Notifier.SendGalaxyTestAsync());
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}");
            }
            finally
            {
                GalaxyTestButton.IsEnabled = true;
            }
        }

        async void OnRunNowClick(object sender, RoutedEventArgs e)
        {
            if (!_plugin.IsSessionRunning)
            {
                SetStatus("The server session isn't running yet; start the server first.");
                return;
            }

            if (_plugin.Watcher.IsBusy)
            {
                SetStatus("A check is already running.");
                return;
            }

            RunNowButton.IsEnabled = false;
            SetStatus("Running diagnostics; this takes a few minutes...");
            try
            {
                var outcome = await Task.Run(() => _plugin.Watcher.CheckAsync(true, CancellationToken.None));
                SetStatus($"Finished. Sim speed avg {outcome.Sample.Avg:0.00}. Report: {outcome.ReportPath}");
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}");
            }
            finally
            {
                RunNowButton.IsEnabled = true;
            }
        }

        void OnOpenReportsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = _plugin.Watcher.ReportDirectory;
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", $"\"{dir}\"");
            }
            catch (Exception ex)
            {
                SetStatus($"Couldn't open the folder: {ex.Message}");
            }
        }

        void OnReloadClick(object sender, RoutedEventArgs e)
        {
            _plugin.ReloadConfig();
            RefreshProfilerStatus();
            SetStatus("Settings reloaded from SimWatch.cfg.");
        }
    }
}
