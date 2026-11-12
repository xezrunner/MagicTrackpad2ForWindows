using System;
using System.Globalization;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    /// <summary>
    /// Smoothing state carried across an automatic application restart.
    /// </summary>
    internal readonly record struct SmoothingRecovery(int RateHz, int FallbackHz, int Attempt)
    {
        private const string RatePrefix = "--resume-smoothing-rate=";
        private const string FallbackPrefix = "--resume-smoothing-fallback=";
        private const string AttemptPrefix = "--smoothing-restart-attempt=";

        internal int DelaySeconds => Math.Min(60, 1 << Math.Min(Attempt, 6));

        internal string Arguments =>
            FormattableString.Invariant($"{RatePrefix}{RateHz} {FallbackPrefix}{FallbackHz} {AttemptPrefix}{Attempt} --background");

        internal static SmoothingRecovery ForRestart(int rateHz, int fallbackHz, int previousAttempt)
        {
            return new SmoothingRecovery(rateHz, rateHz == 0 ? fallbackHz : 0, Math.Min(previousAttempt + 1, 6));
        }

        // Only this app writes these arguments. The window checks the rate against its list.
        internal static SmoothingRecovery? Parse(string[] arguments)
        {
            int? rate = Read(arguments, RatePrefix);
            if (!rate.HasValue)
                return null;

            int fallback = rate.Value == 0 ? Math.Clamp(Read(arguments, FallbackPrefix) ?? 0, 60, 1000) : 0;
            int attempt = Math.Clamp(Read(arguments, AttemptPrefix) ?? 1, 1, 6);
            return new SmoothingRecovery(rate.Value, fallback, attempt);
        }

        private static int? Read(string[] arguments, string prefix)
        {
            foreach (string argument in arguments)
            {
                if (argument.StartsWith(prefix, StringComparison.Ordinal) &&
                    int.TryParse(argument[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                    return value;
            }

            return null;
        }
    }
}
