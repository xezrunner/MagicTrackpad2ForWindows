using System;
using System.Globalization;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    internal readonly record struct SmoothingRecovery(int RateHz, int FallbackHz, int Attempt)
    {
        internal int DelaySeconds => Math.Min(60, 1 << Math.Min(Attempt, 6));
        internal string Arguments => FormattableString.Invariant($"--resume-smoothing-rate={RateHz} --resume-smoothing-fallback={FallbackHz} --smoothing-restart-attempt={Attempt} --background");

        internal static SmoothingRecovery ForRestart(int rateHz, int fallbackHz, int previousAttempt) =>
            new SmoothingRecovery(rateHz, rateHz == 0 ? fallbackHz : 0, Math.Min(previousAttempt + 1, 6));

        internal static SmoothingRecovery? Parse(string[] arguments)
        {
            int? rate = null;
            int fallback = 0, attempt = 1;
            foreach (string argument in arguments)
            {
                const string ratePrefix = "--resume-smoothing-rate=";
                const string fallbackPrefix = "--resume-smoothing-fallback=";
                const string attemptPrefix = "--smoothing-restart-attempt=";
                if (argument.StartsWith(ratePrefix, StringComparison.Ordinal))
                {
                    if (!Number(argument, ratePrefix, out int value) || !Rate(value)) return null;
                    rate = value;
                }
                else if (argument.StartsWith(fallbackPrefix, StringComparison.Ordinal))
                {
                    if (!Number(argument, fallbackPrefix, out fallback) || !Rate(fallback)) return null;
                }
                else if (argument.StartsWith(attemptPrefix, StringComparison.Ordinal))
                {
                    if (!Number(argument, attemptPrefix, out attempt) || attempt < 1 || attempt > 6) return null;
                }
            }
            if (!rate.HasValue || (rate.Value != 0 && fallback != 0)) return null;
            return new SmoothingRecovery(rate.Value, fallback, attempt);
        }

        private static bool Rate(int value) => value == 0 || (value >= 60 && value <= 1000);
        private static bool Number(string argument, string prefix, out int value) =>
            int.TryParse(argument[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
