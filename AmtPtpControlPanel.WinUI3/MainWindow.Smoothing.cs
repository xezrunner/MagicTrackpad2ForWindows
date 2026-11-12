using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using AmtPtpControlPanel.WinUI3.Smoothing;
using AmtPtpControlPanel.WinUI3.Tray;

namespace AmtPtpControlPanel.WinUI3
{
    /// <summary>
    /// Pointer smoothing, tray icon, and Quit handling for the main window.
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly SmoothingController _smoothing = new SmoothingController();
        private readonly SmoothingPowerPolicy _smoothingPower = new SmoothingPowerPolicy();
        private readonly DispatcherTimer _smoothingTimer = new DispatcherTimer();
        private readonly SmoothingRecovery? _recovery;
        private readonly bool _backgroundLaunch;

        private NativeTray? _tray;
        private bool _smoothingAvailable;
        private bool _smoothingChecked;
        private int _smoothingRateHz;

        private bool _closing;
        private bool _closeReady;
        private bool _restarting;
        private int _restartAttempt;
        private long _restartAfter;
        private long _trayRetryAfter;

        internal MainWindow(SmoothingRecovery? recovery, bool backgroundLaunch)
            : this()
        {
            _recovery = recovery;
            _backgroundLaunch = backgroundLaunch;
            _restartAttempt = recovery?.Attempt ?? 0;
        }

        private async Task LoadSmoothingAsync()
        {
            _smoothingAvailable = await Task.Run(PointerSmoothing.IsAvailable);
            _smoothingChecked = true;
            if (_closing)
                return;

            EnsureTray();

            bool enabled;
            SmoothingSettings.Load(out enabled, out _smoothingRateHz);
            if (_recovery.HasValue)
            {
                enabled = true;
                _smoothingRateHz = _recovery.Value.RateHz;
            }

            if (!SelectSmoothingRate(_smoothingRateHz))
            {
                _smoothingRateHz = 0;
                SelectSmoothingRate(0);
            }
            EnableSmoothing.IsChecked = enabled;

            if (enabled && _smoothingAvailable)
            {
                _smoothingPower.SetEnabled(true);
                if (_recovery.HasValue)
                    _smoothing.Configure(true, _smoothingRateHz, _recovery.Value.FallbackHz, _recovery.Value.DelaySeconds);
                else
                    _smoothing.Configure(true, _smoothingRateHz);
            }

            if (_backgroundLaunch && _tray?.IsVisible == true)
                AppWindow.Hide();
            else if (_backgroundLaunch)
                AppWindow.Show(false);

            UpdateSmoothingStatus();
            _smoothingTimer.Tick += (s, e) => UpdateSmoothingStatus();
            _smoothingTimer.Start();
        }

        private bool SelectSmoothingRate(int rateHz)
        {
            string tag = rateHz.ToString(CultureInfo.InvariantCulture);
            foreach (ComboBoxItem item in SmoothingRate.Items)
            {
                if ((string)item.Tag == tag)
                {
                    SmoothingRate.SelectedItem = item;
                    return true;
                }
            }

            return false;
        }

        private async Task ApplySmoothingAsync()
        {
            if (_closing || !_smoothingChecked)
                return;

            bool enabled = EnableSmoothing.IsChecked == true;
            if (SmoothingRate.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                _smoothingRateHz = int.Parse(tag, CultureInfo.InvariantCulture);

            try
            {
                SmoothingSettings.Save(enabled, _smoothingRateHz);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(
                    T("Message.Error", "Error"),
                    string.Format(T("Message.RegistryWriteError", "Error writing to the registry: {0}"), ex.Message));
            }

            SetSmoothingEnabled(enabled);
        }

        private void ToggleSmoothingFromTray()
        {
            bool enabled = !_smoothing.Enabled;
            EnableSmoothing.IsChecked = enabled;
            try
            {
                SmoothingSettings.Save(enabled, _smoothingRateHz);
            }
            catch (Exception ex)
            {
                // The tray cannot show an error dialog, so log the failure and keep going.
                Debug.WriteLine("Saving smoothing: " + ex.Message);
            }

            SetSmoothingEnabled(enabled);
        }

        private void SetSmoothingEnabled(bool enabled)
        {
            if (_closing)
                return;

            if (enabled && (!_smoothingChecked || !_smoothingAvailable))
                return;

            _restartAttempt = 0;
            if (enabled)
                _smoothingPower.SetEnabled(true);

            _smoothing.Configure(enabled, _smoothingRateHz);

            if (!enabled)
                _smoothingPower.SetEnabled(false);

            UpdateSmoothingStatus();
        }

        private void UpdateSmoothingStatus()
        {
            if (!_smoothingChecked)
                return;

            if (!_closing)
                EnsureTray();

            if (_smoothing.HasHealthyRun)
                _restartAttempt = 0;

            _smoothingPower.SetEnabled(_smoothing.Enabled);

            // Poll quickly only while someone can see the status change.
            TimeSpan interval = TimeSpan.FromMilliseconds(AppWindow.IsVisible && _smoothing.Enabled ? 100 : 1000);
            if (_smoothingTimer.Interval != interval)
                _smoothingTimer.Interval = interval;

            EnableSmoothing.IsEnabled = _smoothingAvailable && !_closing && !_restarting;
            SmoothingRate.IsEnabled = EnableSmoothing.IsEnabled;

            // The checkbox already shows plain on and off, so the title names only other states.
            string status = GetSmoothingStatus();
            bool off = !_smoothing.Enabled && !_smoothing.IsStopping;
            bool running = _smoothing.Enabled && _smoothing.IsRunning && !_smoothing.IsPaused && !_smoothing.UsingFallback;
            bool plain = _smoothingAvailable && !_closing && (off || running);
            ShowSmoothingStatus(plain ? "" : status);
            _tray?.Update(status);

            // Without a tray icon, a hidden window would leave no way back to the app.
            if (!_closing && _tray != null && !_tray.IsVisible && !AppWindow.IsVisible)
                AppWindow.Show(false);

            if (_smoothing.Enabled && _smoothing.RestartRequired && !_restarting && Stopwatch.GetTimestamp() >= _restartAfter)
                RestartForRecovery();
        }

        private string GetSmoothingStatus()
        {
            if (!_smoothingAvailable)
                return T("Smoothing.Unavailable", "Unavailable");

            if (_closing || (!_smoothing.Enabled && _smoothing.IsStopping))
                return T("Smoothing.Stopping", "Stopping…");

            if (!_smoothing.Enabled)
                return T("Smoothing.Stopped", "Stopped");

            if (_smoothing.IsPaused)
                return T("Smoothing.Paused", "Waiting for Windows…");

            if (_smoothing.IsRunning && _smoothing.UsingFallback)
                return T("Smoothing.RunningFallback", "Running with timer fallback");

            if (_smoothing.IsRunning)
                return T("Smoothing.Running", "Running");

            if (_smoothing.LastReason == SmoothingStop.DuplicateOwner)
                return T("Smoothing.OwnedElsewhere", "Waiting for another instance…");

            return T("Smoothing.Recovering", "Reconnecting…");
        }

        private void ShowSmoothingStatus(string status)
        {
            string title = T("Section.Smoothing", "Smoothing");
            if (status.Length != 0)
                title = string.Format("{0} ({1})", title, status);

            if (SmoothingTitle.Text == title)
                return;

            SmoothingTitle.Text = title;
            FrameworkElementAutomationPeer.CreatePeerForElement(SmoothingTitle)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void EnsureTray()
        {
            if (_tray != null || Stopwatch.GetTimestamp() < _trayRetryAfter)
                return;

            _trayRetryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            try
            {
                _tray = new NativeTray(
                    WinRT.Interop.WindowNative.GetWindowHandle(this),
                    OpenFromTray,
                    ToggleSmoothingFromTray,
                    QuitAsync,
                    () => _smoothing.Enabled,
                    (ready, reset) => _smoothing.SetEnvironmentReady(ready, reset),
                    CopySmoothingDiagnostics);
            }
            catch (Win32Exception ex)
            {
                // The tray icon is retried on the next status update.
                Debug.WriteLine("Tray startup: " + ex.Message);
            }
        }

        private void CopySmoothingDiagnostics()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine($"Enabled: {_smoothing.Enabled}; Running: {_smoothing.IsRunning}; Paused: {_smoothing.IsPaused}");
            text.AppendLine($"Rate: {_smoothingRateHz}; Timer fallback: {_smoothing.UsingFallback}");
            text.AppendLine($"Scheduling override: {_smoothingPower.IsApplied}; Scheduling error: {_smoothingPower.Error}");
            foreach (SmoothingInterruption entry in _smoothing.GetInterruptions())
                text.AppendLine($"{entry.Time:O} {entry.Reason}: {entry.Detail}");

            DataPackage content = new DataPackage();
            content.SetText(text.ToString());
            Clipboard.SetContent(content);
            Clipboard.Flush();
        }

        private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_closeReady)
                return;

            // Closing keeps smoothing running; Quit is the way to exit.
            args.Cancel = true;
            if (_tray?.IsVisible == true)
                AppWindow.Hide();
            else if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Minimize();
        }

        private void OpenFromTray()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
                presenter.Restore();

            AppWindow.Show();
            Activate();
            UpdateSmoothingStatus();
        }

        private async void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            await QuitAsync();
        }

        private async Task QuitAsync()
        {
            if (_closing)
                return;

            _closing = true;
            EnableSmoothing.IsEnabled = false;
            SmoothingRate.IsEnabled = false;
            ShowSmoothingStatus(T("Smoothing.Stopping", "Stopping…"));

            // Stopping disables interception immediately. A stuck background
            // clock must not prevent the app from exiting.
            Task shutdown = _smoothing.ShutdownAsync();
            _smoothingPower.SetEnabled(false);
            await Task.WhenAny(shutdown, Task.Delay(1000));

            _smoothingTimer.Stop();
            _tray?.Dispose();
            _closeReady = true;
            DispatcherQueue.TryEnqueue(() => Close());
        }

        private void RestartForRecovery()
        {
            _restarting = true;
            EnableSmoothing.IsEnabled = false;
            SmoothingRate.IsEnabled = false;

            SmoothingRecovery recovery = SmoothingRecovery.ForRestart(_smoothingRateHz, _smoothing.RecoveryFallbackRate, _restartAttempt);
            try
            {
                // A restart is needed only when failed native cleanup prevents
                // replacing the input hook safely inside this process.
                _tray?.Dispose();
                _tray = null;
                Windows.ApplicationModel.Core.AppRestartFailureReason failure =
                    Microsoft.Windows.AppLifecycle.AppInstance.Restart(recovery.Arguments);
                _smoothing.RecordRestartFailure("Application restart returned: " + failure);
            }
            catch (Exception ex)
            {
                _smoothing.RecordRestartFailure("Application restart failed: " + ex.Message);
            }
            finally
            {
                _restarting = false;
                _restartAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
                _trayRetryAfter = 0;
                if (!_closing)
                    EnsureTray();
            }
        }
    }
}
