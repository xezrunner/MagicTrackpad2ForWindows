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

        internal static SmoothingRecovery? Parse(string[] arguments)
        {
            int? rate = null;
            int fallback = 0;
            int attempt = 1;
            foreach (string argument in arguments)
            {
                if (argument.StartsWith(RatePrefix, StringComparison.Ordinal))
                {
                    if (!Number(argument, RatePrefix, out int value) || !Rate(value))
                        return null;

                    rate = value;
                }
                else if (argument.StartsWith(FallbackPrefix, StringComparison.Ordinal))
                {
                    if (!Number(argument, FallbackPrefix, out fallback) || !Rate(fallback))
                        return null;
                }
                else if (argument.StartsWith(AttemptPrefix, StringComparison.Ordinal))
                {
                    if (!Number(argument, AttemptPrefix, out attempt) || attempt < 1 || attempt > 6)
                        return null;
                }
            }

            if (!rate.HasValue || (rate.Value != 0 && fallback != 0))
                return null;

            return new SmoothingRecovery(rate.Value, fallback, attempt);
        }

        private static bool Rate(int value)
        {
            return value == 0 || (value >= 60 && value <= 1000);
        }

        private static bool Number(string argument, string prefix, out int value)
        {
            return int.TryParse(argument[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }
}
