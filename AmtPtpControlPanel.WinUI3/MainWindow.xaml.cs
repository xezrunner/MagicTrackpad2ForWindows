using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using AmtPtpControlPanel.WinUI3.Smoothing;
using AmtPtpControlPanel.WinUI3.Tray;

namespace AmtPtpControlPanel.WinUI3
{
    public sealed partial class MainWindow : Window
    {
        private bool _updatingLanguageCombo;
        private readonly SmoothingController _smoothing = new SmoothingController();
        private readonly SmoothingPowerPolicy _smoothingPower = new SmoothingPowerPolicy();
        private readonly DispatcherTimer _smoothingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private bool _smoothingAvailable, _smoothingChecked, _updatingSmoothing, _closing, _closeReady;
        private int _smoothingRateHz;
        private readonly SmoothingRecovery? _recovery;
        private readonly bool _backgroundLaunch;
        private int _restartAttempt;
        private NativeTray? _tray;
        private bool _restarting;
        private long _restartAfter;
        private long _trayRetryAfter;

        public MainWindow() : this(null, false) { }

        internal MainWindow(SmoothingRecovery? recovery, bool backgroundLaunch)
        {
            _recovery = recovery;
            _backgroundLaunch = backgroundLaunch;
            _restartAttempt = recovery?.Attempt ?? 0;
            InitializeComponent();

            SystemBackdrop = new MicaBackdrop();
            Title = LocalizationManager.GetString("Main", "Magic Trackpad 2 Control Panel");

            // Extend the Mica backdrop into the title bar and make the header
            // the draggable title area so the caption bar matches the body.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(HeaderPanel);

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindowTitleBar titleBar = AppWindow.TitleBar;
                titleBar.BackgroundColor = Colors.Transparent;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }

            // Use the Magic Trackpad icon for the window (Alt+Tab, Task View,
            // taskbar) instead of the default generated WinUI icon. The .ico is
            // embedded so it also works in single-file deployments.
            try
            {
                string iconPath = Path.Combine(Path.GetTempPath(), "AmtPtpControlPanel.WinUI3.Icon1.ico");
                using (Stream? stream = typeof(MainWindow).Assembly.GetManifestResourceStream("AmtPtpControlPanel.WinUI3.Icon1.ico"))
                {
                    if (stream != null)
                    {
                        using (FileStream file = File.Create(iconPath))
                        {
                            stream.CopyTo(file);
                        }
                        AppWindow.SetIcon(iconPath);
                    }
                }
            }
            catch
            {
                // The icon is cosmetic; keep the default if anything fails.
            }

            RootGrid.Loaded += MainWindow_Loaded;
            RootGrid.ActualThemeChanged += (s, e) => UpdateTitleBarButtonColors();
            _smoothingTimer.Tick += (s, e) => UpdateSmoothingStatus();
            AppWindow.Closing += SmoothingWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTitleBarButtonColors();
            ApplyLocalization();
            PopulateLanguageCombo();
            LoadSettings();

            _smoothingAvailable = await Task.Run(PointerSmoothing.IsAvailable);
            _smoothingChecked = true;
            if (_closing) return;
            EnsureTray();
            if (_recovery.HasValue && _smoothingAvailable)
            {
                SmoothingRecovery recovery = _recovery.Value;
                _smoothingRateHz = recovery.RateHz;
                _updatingSmoothing = true;
                foreach (ComboBoxItem item in SmoothingRate.Items)
                    if ((string)item.Tag == _smoothingRateHz.ToString(CultureInfo.InvariantCulture)) SmoothingRate.SelectedItem = item;
                _updatingSmoothing = false;
                _smoothingPower.SetEnabled(true);
                _smoothing.Configure(true, _smoothingRateHz, recovery.FallbackHz, recovery.DelaySeconds);
            }
            if (_backgroundLaunch && _tray?.IsVisible == true) AppWindow.Hide();
            else if (_backgroundLaunch) AppWindow.Show(false);
            UpdateSmoothingStatus();
            _smoothingTimer.Start();

            await UpdateBatteryAsync(showErrorOnFailure: false);
        }

        private void SmoothingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingSmoothing || _closing) return;
            SetSmoothingEnabled(SmoothingToggle.IsOn);
        }

        private void SmoothingRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_smoothingChecked || _closing || _updatingSmoothing) return;
            _restartAttempt = 0;
            _smoothingRateHz = int.Parse((string)((ComboBoxItem)SmoothingRate.SelectedItem).Tag, CultureInfo.InvariantCulture);
            _smoothing.Configure(_smoothing.Enabled, _smoothingRateHz);
            UpdateSmoothingStatus();
        }

        private void SetSmoothingEnabled(bool enabled)
        {
            if (_closing || (enabled && (!_smoothingChecked || !_smoothingAvailable))) return;
            _restartAttempt = 0;
            if (enabled) _smoothingPower.SetEnabled(true);
            _smoothing.Configure(enabled, _smoothingRateHz);
            if (!enabled) _smoothingPower.SetEnabled(false);
            UpdateSmoothingStatus();
        }

        private void UpdateSmoothingStatus()
        {
            if (!_smoothingChecked) return;
            if (!_closing) EnsureTray();
            if (_smoothing.HasHealthyRun) _restartAttempt = 0;
            _smoothingPower.SetEnabled(_smoothing.Enabled);
            var interval = TimeSpan.FromMilliseconds(AppWindow.IsVisible && _smoothing.Enabled ? 100 : 1000);
            if (_smoothingTimer.Interval != interval) _smoothingTimer.Interval = interval;
            _updatingSmoothing = true;
            SmoothingToggle.IsOn = _smoothing.Enabled;
            _updatingSmoothing = false;
            SmoothingToggle.IsEnabled = _smoothingAvailable && !_closing && !_restarting;
            SmoothingRate.IsEnabled = SmoothingToggle.IsEnabled;
            string state = "Stopped";
            if (!_smoothingAvailable) state = "Unavailable";
            else if (_closing) state = "Stopping";
            else if (_smoothing.Enabled)
                state = _smoothing.IsPaused ? "Paused" :
                    _smoothing.IsRunning ? (_smoothing.UsingFallback ? "RunningFallback" : "Running") :
                    _smoothing.LastReason == SmoothingStop.DuplicateOwner ? "OwnedElsewhere" : "Recovering";
            else if (_smoothing.IsStopping) state = "Stopping";
            string fallback = state switch
            {
                "Unavailable" => "Smoothing is unavailable on this system.",
                "Recovering" => "Reconnecting smoothing…",
                "Paused" => "Waiting for Windows…",
                "OwnedElsewhere" => "Waiting for another instance…",
                "RunningFallback" => "Running with timer fallback",
                "Running" => "Running",
                "Stopping" => "Stopping…",
                _ => "Stopped"
            };
            SetSmoothingStatus(T("Smoothing." + state, fallback));
            _tray?.Update(SmoothingStatus.Text);
            if (!_closing && _tray != null && !_tray.IsVisible && !AppWindow.IsVisible) AppWindow.Show(false);
            if (_smoothing.Enabled && _smoothing.RestartRequired && !_restarting && Stopwatch.GetTimestamp() >= _restartAfter)
                RestartForRecovery();
        }

        private void EnsureTray()
        {
            if (_tray != null || Stopwatch.GetTimestamp() < _trayRetryAfter) return;
            _trayRetryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            try
            {
                _tray = new NativeTray(WinRT.Interop.WindowNative.GetWindowHandle(this), OpenFromTray,
                    () => SetSmoothingEnabled(!_smoothing.Enabled), QuitAsync, () => _smoothing.Enabled,
                    (ready, reset) => _smoothing.SetEnvironmentReady(ready, reset), CopySmoothingDiagnostics);
            }
            catch (System.ComponentModel.Win32Exception ex) { Debug.WriteLine("Tray startup: " + ex.Message); }
        }

        private void SetSmoothingStatus(string text)
        {
            if (SmoothingStatus.Text == text) return;
            SmoothingStatus.Text = text;
            FrameworkElementAutomationPeer.CreatePeerForElement(SmoothingStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void CopySmoothingDiagnostics()
        {
            var text = new StringBuilder();
            text.AppendLine($"Enabled: {_smoothing.Enabled}; Running: {_smoothing.IsRunning}; Paused: {_smoothing.IsPaused}");
            text.AppendLine($"Rate: {_smoothingRateHz}; Timer fallback: {_smoothing.UsingFallback}");
            text.AppendLine($"Scheduling override: {_smoothingPower.IsApplied}; Scheduling error: {_smoothingPower.Error}");
            foreach (var entry in _smoothing.GetInterruptions()) text.AppendLine($"{entry.Time:O} {entry.Reason}: {entry.Detail}");
            var content = new Windows.ApplicationModel.DataTransfer.DataPackage();
            content.SetText(text.ToString());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }

        private void SmoothingWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_closeReady) return;
            args.Cancel = true;
            if (_tray?.IsVisible == true) AppWindow.Hide();
            else if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
        }

        private void OpenFromTray()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
                presenter.Restore();
            AppWindow.Show();
            Activate();
            UpdateSmoothingStatus();
        }

        private async Task QuitAsync()
        {
            if (_closing) return;
            _closing = true;
            SmoothingToggle.IsEnabled = false;
            SmoothingRate.IsEnabled = false;
            SetSmoothingStatus(T("Smoothing.Stopping", "Stopping…"));
            // NOTE: Stop disables interception immediately. A stuck background clock must not prevent app exit.
            Task shutdown = _smoothing.ShutdownAsync();
            _smoothingPower.SetEnabled(false);
            await Task.WhenAny(shutdown, Task.Delay(1000));
            _smoothingTimer.Stop();
            _tray?.Dispose();
            _closeReady = true;
            DispatcherQueue.TryEnqueue(() => Close());
        }

        private async void QuitButton_Click(object sender, RoutedEventArgs e) => await QuitAsync();

        private void RestartForRecovery()
        {
            _restarting = true;
            SmoothingToggle.IsEnabled = false;
            SmoothingRate.IsEnabled = false;
            SmoothingRecovery recovery = SmoothingRecovery.ForRestart(_smoothingRateHz, _smoothing.RecoveryFallbackRate, _restartAttempt);
            try
            {
                // NOTE: Restart is needed only when a failed native cleanup prevents safe hook replacement.
                _tray?.Dispose();
                _tray = null;
                var failure = Microsoft.Windows.AppLifecycle.AppInstance.Restart(recovery.Arguments);
                _smoothing.RecordRestartFailure("Application restart returned: " + failure);
            }
            catch (Exception ex) { _smoothing.RecordRestartFailure("Application restart failed: " + ex.Message); }
            finally
            {
                _restarting = false;
                _restartAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
                _trayRetryAfter = 0;
                if (!_closing) EnsureTray();
            }
        }

        private void PopulateLanguageCombo()
        {
            _updatingLanguageCombo = true;

            LanguageComboBox.Items.Clear();
            foreach (string code in LocalizationManager.AvailableLanguages)
            {
                LanguageComboBox.Items.Add(new ComboBoxItem
                {
                    Content = GetLanguageDisplayName(code),
                    Tag = code
                });
            }

            foreach (ComboBoxItem item in LanguageComboBox.Items)
            {
                if (string.Equals(item.Tag as string, LocalizationManager.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageComboBox.SelectedItem = item;
                    break;
                }
            }

            _updatingLanguageCombo = false;
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingLanguageCombo)
                return;

            if (LanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string code)
            {
                LocalizationManager.SetLanguage(code);
                ApplyLocalization();
            }
        }

        private static string GetLanguageDisplayName(string code)
        {
            return code switch
            {
                "it" => "Italiano",
                "es" => "Español",
                "fr" => "Français",
                "de" => "Deutsch",
                "pt" => "Português",
                "ru" => "Русский",
                "zh" => "中文",
                "ja" => "日本語",
                "ko" => "한국어",
                _ => "English"
            };
        }

        private void UpdateTitleBarButtonColors()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            Windows.UI.Color foreground = dark ? Colors.White : Colors.Black;

            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = Colors.Gray;
        }

        private static string T(string key, string fallback) =>
            LocalizationManager.GetString(key, fallback);

        private void ApplyLocalization()
        {
            Title = T("Main", "Magic Trackpad 2 Control Panel");
            TitleText.Text = Title;

            ClickFeedbackTitle.Text = T("Section.ClickFeedbackMacOS", "MacOS click options");
            MacOSClickOptions.Content = T("ctlMacOSClickOptions", "Use macOS Click Options:");
            SilentClicking.Content = T("ctlSilentClicking", "Silent clicking");
            ClickStrengthLabel.Text = T("Section.ClickStrength", "Click strength");
            LightLabel.Text = T("ctlLightLabel", "Light");
            MediumLabel.Text = T("ctlMediumLabel", "Medium");
            FirmLabel.Text = T("ctlFirmLabel", "Firm");

            ClickFeedbackOtherTitle.Text = T("Section.ClickFeedbackOther", "Click options not available in macOS");
            DisableFeedback.Content = T("ctlDisableFeedback", "Disable haptic feedback and force touch button completely");
            MaximumFeedback.Content = T("ctlMaximumFeedback", "Maximum haptic feedback (very clicky and loud!)");

            LiftFingerTitle.Text = T("groupBox3", "When you lift your finger from the trackpad");
            StopDoNothing.Content = T("ctlStopDoNothing", "Do nothing");
            StopPressureText.Text = T("ctlStopPressure", "Stop the pointer if the pressure is less than or equal to");
            StopPressureLabelText.Text = T("ctlStopPressureLabel", "units. (0 means no pressure; 0 is a good value)");
            StopSizeText.Text = T("ctlStopSize", "Stop the pointer if the size of the touch area is less than or equal to");
            StopSizeLabelText.Text = T("ctlStopSizeLabel", "units. (7 is a good value)");

            OtherOptionsTitle.Text = T("groupBox4", "Other options");
            IgnoreNearFingers.Content = T("ctlIgnoreNearFingers", "Ignore input from fingers not touching the trackpad surface");
            IgnoreButtonFingerText.Text = T("ctlIgnoreButtonFinger", "Ignore input from the finger used to click the force touch button (useful for dragging, if you use your thumb to click the button and your index finger to move the pointer, for example)");
            PalmRejection.Content = T("ctlPalmRejection", "Palm Rejection");
            InvertMovementText.Text = T("ctlInvertMovement", "Invert up/down and left/right (for upside-down use)");
            AllowNonAdminAccessText.Text = T("ctlAllowNonAdminAccess", "ADVANCED: Let non-admin apps communicate with the drivers");
            LanguageLabel.Text = T("ctlLanguage", "UI language");

            BatteryTitle.Text = T("Section.Battery", "Battery");
            SmoothingTitle.Text = T("Section.Smoothing", "Smoothing");
            SmoothingToggle.Header = T("Smoothing.Enable", "Enable smoothing");
            SmoothingRate.Header = T("Smoothing.Rate", "Update rate");
            SmoothingAutomatic.Content = T("Smoothing.Automatic", "Automatic (display)");
            SmoothingScope.Text = T("Smoothing.Scope", "Affects all mice. Closing this window keeps smoothing in the tray.");
            QuitButton.Content = T("Smoothing.Quit", "Quit");
            UpdateSmoothingStatus();
            UpdateBatteryButton.Content = T("ctlBatteryUpdate", "Update Battery");

            TouchpadSettingsButton.Content = T("ctlTouchpadSettings", "Windows Touchpad Settings");
            ApplyButton.Content = T("ctlApply", "Apply");
            GitHubLink.Content = T("Link.GitHub", "Project on GitHub");
        }

        private void LoadSettings()
        {
            TrackpadSettings s = SettingsService.Load();

            if (s.ButtonDisabled != 0)
            {
                DisableFeedback.IsChecked = true;
            }
            else if (s.FeedbackClick == 0xffffff && s.FeedbackRelease == 0xffffff)
            {
                MaximumFeedback.IsChecked = true;
            }
            else
            {
                MacOSClickOptions.IsChecked = true;

                if ((s.FeedbackClick & 0xffff00) == 0 && (s.FeedbackRelease & 0xffff00) == 0)
                    SilentClicking.IsChecked = true;

                if ((s.FeedbackClick & 0x0000ff) == 0x15 && (s.FeedbackRelease & 0x0000ff) == 0x10)
                    FeedbackSlider.Value = 0;
                else if ((s.FeedbackClick & 0x0000ff) == 0x1e && (s.FeedbackRelease & 0x0000ff) == 0x18)
                    FeedbackSlider.Value = 2;
                else
                    FeedbackSlider.Value = 1;
            }

            if (s.StopPressure == -1 && s.StopSize == -1)
            {
                StopDoNothing.IsChecked = true;
            }
            else if (s.StopPressure != -1)
            {
                StopPressure.IsChecked = true;
                StopPressureValue.Value = s.StopPressure;
            }
            else
            {
                StopSize.IsChecked = true;
                StopSizeValue.Value = s.StopSize;
            }

            IgnoreButtonFinger.IsChecked = s.IgnoreButtonFinger != 0;
            IgnoreNearFingers.IsChecked = s.IgnoreNearFingers != 0;
            PalmRejection.IsChecked = s.PalmRejection != 0;
            InvertMovement.IsChecked = s.InvertMovement != 0;
            AllowNonAdminAccess.IsChecked = s.AllowNonAdminAccess != 0;
        }

        private void ClickOptions_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton radio || radio.IsChecked != true)
                return;

            if (ReferenceEquals(radio, MacOSClickOptions))
            {
                MaximumFeedback.IsChecked = false;
                DisableFeedback.IsChecked = false;
            }
            else if (ReferenceEquals(radio, MaximumFeedback))
            {
                MacOSClickOptions.IsChecked = false;
                DisableFeedback.IsChecked = false;
            }
            else if (ReferenceEquals(radio, DisableFeedback))
            {
                MacOSClickOptions.IsChecked = false;
                MaximumFeedback.IsChecked = false;
            }

            bool macEnabled = MacOSClickOptions.IsChecked == true;
            SilentClicking.IsEnabled = macEnabled;
            FeedbackSlider.IsEnabled = macEnabled;
        }

        private void StopOptions_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton radio || radio.IsChecked != true)
                return;

            if (ReferenceEquals(radio, StopDoNothing))
            {
                StopPressure.IsChecked = false;
                StopSize.IsChecked = false;
            }
            else if (ReferenceEquals(radio, StopPressure))
            {
                StopDoNothing.IsChecked = false;
                StopSize.IsChecked = false;
            }
            else if (ReferenceEquals(radio, StopSize))
            {
                StopDoNothing.IsChecked = false;
                StopPressure.IsChecked = false;
            }

            StopPressureValue.IsEnabled = StopPressure.IsChecked == true;
            StopSizeValue.IsEnabled = StopSize.IsChecked == true;
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyButton.IsEnabled = false;
            try
            {
                if (await SaveSettingsAsync())
                {
                    UsbDevice.ReloadSettings();
                    BtDevice.SendIoctl(BtDevice.IOCTL_RELOAD_SETTINGS);
                }
            }
            finally
            {
                ApplyButton.IsEnabled = true;
            }
        }

        private void TouchpadSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:devices-touchpad")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // Opening Windows Settings is best-effort only.
            }
        }

        private async void UpdateBatteryButton_Click(object sender, RoutedEventArgs e)
        {
            await UpdateBatteryAsync(showErrorOnFailure: true);
        }

        private async Task UpdateBatteryAsync(bool showErrorOnFailure)
        {
            uint level;
            if (BtDevice.SendIoctl(BtDevice.IOCTL_GET_BATTERY, out level))
                ShowBattery("Bluetooth", level);
            else if (UsbDevice.GetBattery(out level))
                ShowBattery("USB", level);
            else if (showErrorOnFailure)
                await ShowErrorAsync(
                    T("Message.Error", "Error"),
                    T("Message.NoDevice", "No Magic Trackpad 2 connected via USB or Bluetooth found."));
        }

        private void ShowBattery(string source, uint level)
        {
            if (level > 100)
                return;

            BatteryProgressBar.Value = level;

            int bucket = Math.Clamp((int)Math.Round(level / 10.0, MidpointRounding.AwayFromZero), 0, 10);
            int glyphCode = source == "USB" ? 0xF5FD + bucket : 0xF5F2 + bucket;
            BatteryIcon.Glyph = ((char)glyphCode).ToString();

            BatteryTitle.Text = string.Format("{0} ({1}%)", T("Section.Battery", "Battery"), level);
            BatteryStatusText.Text = string.Format(
                T("Battery.LastUpdated", "Battery ({0}) --- LAST UPDATED: {1}"),
                source,
                DateTime.Now.ToString(CultureInfo.CurrentCulture));
        }

        private async Task<bool> SaveSettingsAsync()
        {
            TrackpadSettings s = new TrackpadSettings();

            if (DisableFeedback.IsChecked == true)
            {
                s.ButtonDisabled = 1;
                s.FeedbackClick = 0;
                s.FeedbackRelease = 0;
            }
            else if (MaximumFeedback.IsChecked == true)
            {
                s.ButtonDisabled = 0;
                s.FeedbackClick = 0xffffff;
                s.FeedbackRelease = 0xffffff;
            }
            else
            {
                s.ButtonDisabled = 0;

                int strength = (int)Math.Round(FeedbackSlider.Value);
                s.FeedbackClick = strength == 0 ? 0x040415 : strength == 1 ? 0x060617 : 0x08081e;
                s.FeedbackRelease = strength == 0 ? 0x000010 : strength == 1 ? 0x000014 : 0x020218;

                if (SilentClicking.IsChecked == true)
                {
                    s.FeedbackClick &= 0x0000ff;
                    s.FeedbackRelease &= 0x0000ff;
                }
            }

            if (StopDoNothing.IsChecked == true)
            {
                s.StopPressure = -1;
                s.StopSize = -1;
            }
            else if (StopPressure.IsChecked == true)
            {
                s.StopSize = -1;

                if (!TryReadInt(StopPressureValue, out s.StopPressure) || s.StopPressure < 0)
                {
                    await ShowErrorAsync(
                        T("Message.Error", "Error"),
                        T("Message.PressureInvalid", "Pressure must be greater than or equal to 0."));
                    return false;
                }
            }
            else
            {
                s.StopPressure = -1;

                if (!TryReadInt(StopSizeValue, out s.StopSize) || s.StopSize < 0)
                {
                    await ShowErrorAsync(
                        T("Message.Error", "Error"),
                        T("Message.SizeInvalid", "Size must be greater than or equal to 0."));
                    return false;
                }
            }

            s.IgnoreButtonFinger = IgnoreButtonFinger.IsChecked == true ? 1 : 0;
            s.IgnoreNearFingers = IgnoreNearFingers.IsChecked == true ? 1 : 0;
            s.PalmRejection = PalmRejection.IsChecked == true ? 1 : 0;
            s.InvertMovement = InvertMovement.IsChecked == true ? 1 : 0;
            s.AllowNonAdminAccess = AllowNonAdminAccess.IsChecked == true ? 1 : 0;

            try
            {
                SettingsService.Save(s);
                return true;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(
                    T("Message.Error", "Error"),
                    string.Format(T("Message.RegistryWriteError", "Error writing to the registry: {0}"), ex.Message));
                return false;
            }
        }

        private static bool TryReadInt(NumberBox box, out int value)
        {
            value = 0;

            double v = box.Value;
            if (double.IsNaN(v))
                return false;

            value = (int)Math.Round(v);
            return true;
        }

        private async Task ShowErrorAsync(string title, string message)
        {
            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close
            };

            await dialog.ShowAsync();
        }
    }
}
