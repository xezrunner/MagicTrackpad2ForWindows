using Microsoft.Win32;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    /// <summary>
    /// Reads and writes the applied smoothing choice in HKCU, next to the UI language.
    /// </summary>
    internal static class SmoothingSettings
    {
        private const string RegistryPath = @"Software\AmtPtpControlPanel";
        private const string EnabledValueName = "SmoothingEnabled";
        private const string RateValueName = "SmoothingRate";

        public static void Load(out bool enabled, out int rateHz)
        {
            enabled = false;
            rateHz = 0;

            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    if (key == null)
                        return;

                    if (key.GetValue(EnabledValueName) is int savedEnabled)
                        enabled = savedEnabled != 0;

                    if (key.GetValue(RateValueName) is int savedRate)
                        rateHz = savedRate;
                }
            }
            catch
            {
                // Fall back to smoothing off if the saved choice cannot be read.
            }
        }

        public static void Save(bool enabled, int rateHz)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true))
            {
                key.SetValue(EnabledValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue(RateValueName, rateHz, RegistryValueKind.DWord);
            }
        }
    }
}
