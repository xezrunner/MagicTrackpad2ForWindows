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
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;
using AmtPtpControlPanel.WinUI3.Smoothing;

namespace AmtPtpControlPanel.WinUI3
{
    /// <summary>
    /// Pointer smoothing, tray icon, and Quit handling for the main window.
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly SmoothingController _smoothing = new SmoothingController();
        private readonly DispatcherTimer _smoothingTimer = new DispatcherTimer();
        private readonly bool _smoothingAvailable = Environment.Is64BitProcess;
        private readonly SmoothingRecovery? _recovery;

        private NativeTray? _tray;
        private bool _smoothingLoaded;
        private int _smoothingRateHz;

        private bool _closing;
        private bool _closeReady;
        private long _restartAfter;

        internal MainWindow(SmoothingRecovery? recovery)
            : this()
        {
            _recovery = recovery;
        }

        private void LoadSmoothing()
        {
            if (_smoothingAvailable)
                SmoothingPowerPolicy.Apply();

            try
            {
                _tray = new NativeTray(
                    WindowNative.GetWindowHandle(this),
                    OpenFromTray,
                    ToggleSmoothingFromTray,
                    QuitAsync,
                    () => _smoothing.Enabled,
                    (ready, reset) => _smoothing.SetEnvironmentReady(ready, reset),
                    CopySmoothingDiagnostics);
            }
            catch (Win32Exception ex)
            {
                // Close minimizes the window instead of hiding it.
                Debug.WriteLine("Tray startup: " + ex.Message);
            }

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

            // The first status update lets the tray report a locked session before the first start.
            _smoothingLoaded = true;
            UpdateSmoothingStatus();
            if (enabled && _smoothingAvailable)
                _smoothing.Configure(true, _smoothingRateHz, _recovery);

            _smoothingTimer.Tick += SmoothingTimer_Tick;
            _smoothingTimer.Start();
            SmoothingTimer_Tick(null, null);
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
            if (!_smoothingAvailable)
                return;

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
            if (enabled && !_smoothingAvailable)
                return;

            _smoothing.Configure(enabled, _smoothingRateHz);
            UpdateSmoothingStatus();
        }

        private void SmoothingTimer_Tick(object? sender, object? e)
        {
            UpdateSmoothingStatus();

            // Without a tray icon, a hidden window would leave no way back to the app.
            if (!_closing && _tray?.IsVisible != true && !AppWindow.IsVisible)
                AppWindow.Show(false);

            // A restart is needed only when failed native cleanup prevents
            // replacing the input hook safely inside this process.
            if (_smoothing.Enabled && _smoothing.RestartRequired && Stopwatch.GetTimestamp() >= _restartAfter)
                RestartForRecovery();
        }

        private void UpdateSmoothingStatus()
        {
            if (!_smoothingLoaded)
                return;

            // Poll quickly only while someone can see the status change.
            TimeSpan interval = TimeSpan.FromMilliseconds(AppWindow.IsVisible && _smoothing.Enabled ? 100 : 1000);
            if (_smoothingTimer.Interval != interval)
                _smoothingTimer.Interval = interval;

            EnableSmoothing.IsEnabled = _smoothingAvailable && !_closing;
            SmoothingRate.IsEnabled = EnableSmoothing.IsEnabled;

            // The checkbox already shows plain on and off, so the title names only other states.
            string status = GetSmoothingStatus();
            bool off = !_smoothing.Enabled && !_smoothing.IsStopping;
            bool running = _smoothing.Enabled && !_smoothing.IsPaused && !_smoothing.UsingFallback &&
                (_smoothing.IsRunning || !_smoothing.HasFailed);
            bool plain = _smoothingAvailable && !_closing && (off || running);
            ShowSmoothingStatus(plain ? "" : status);
            _tray?.Update(status);
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

            // A start shows as running until something has failed.
            bool running = _smoothing.IsRunning || !_smoothing.HasFailed;
            if (running && _smoothing.UsingFallback)
                return T("Smoothing.RunningFallback", "Running with timer fallback");

            if (running)
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

        private void CopySmoothingDiagnostics()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine($"Enabled: {_smoothing.Enabled}; Running: {_smoothing.IsRunning}; Paused: {_smoothing.IsPaused}");
            text.AppendLine($"Rate: {_smoothingRateHz}; Timer fallback: {_smoothing.UsingFallback}");
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

            // Stopping disables interception immediately. A stuck background
            // clock must not prevent the app from exiting.
            Task shutdown = _smoothing.ShutdownAsync();
            UpdateSmoothingStatus();
            await Task.WhenAny(shutdown, Task.Delay(1000));

            _smoothingTimer.Stop();
            _tray?.Dispose();
            _closeReady = true;
            DispatcherQueue.TryEnqueue(() => Close());
        }

        private void RestartForRecovery()
        {
            try
            {
                // The new process adds its own icon.
                _tray?.RemoveIcon();
                AppRestartFailureReason failure = AppInstance.Restart(_smoothing.CreateRecovery().Arguments);
                _smoothing.RecordRestartFailure("Application restart returned: " + failure);
            }
            catch (Exception ex)
            {
                _smoothing.RecordRestartFailure("Application restart failed: " + ex.Message);
            }

            _restartAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        }
    }
}
