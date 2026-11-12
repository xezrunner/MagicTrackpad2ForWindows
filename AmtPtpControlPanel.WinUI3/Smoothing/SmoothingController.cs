using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    /// <summary>
    /// One recorded interruption of a smoothing run.
    /// </summary>
    internal readonly record struct SmoothingInterruption(DateTime Time, SmoothingStop Reason, string? Detail);

    /// <summary>
    /// Keeps smoothing in the requested state, restarting the worker after interruptions.
    /// </summary>
    internal sealed class SmoothingController
    {
        private readonly PointerSmoothing _worker = new PointerSmoothing();
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _changed = new SemaphoreSlim(0, 1);
        private readonly SmoothingInterruption[] _history = new SmoothingInterruption[32];
        private Task? _loop;

        private bool _enabled;
        private bool _quit;
        private bool _cleanupStalled;
        private bool _environmentReady = true;
        private int _rateHz;
        private int _version;
        private int _interruptions;
        private int _fallbackRate;
        private SmoothingStop _lastReason;
        private long _notBefore;
        private long _runningSince;
        private bool _monitoringRun;
        private bool _healthyRun;

        internal bool Enabled
        {
            get
            {
                lock (_gate)
                    return _enabled;
            }
        }

        internal bool IsRunning => _worker.IsRunning;

        internal bool IsStopping => _worker.IsStopping;

        internal bool IsPaused
        {
            get
            {
                lock (_gate)
                    return _enabled && !_environmentReady;
            }
        }

        internal bool UsingFallback
        {
            get
            {
                lock (_gate)
                    return _fallbackRate != 0;
            }
        }

        internal SmoothingStop LastReason
        {
            get
            {
                lock (_gate)
                    return _lastReason;
            }
        }

        internal int RecoveryFallbackRate
        {
            get
            {
                lock (_gate)
                {
                    if (_rateHz != 0)
                        return 0;

                    return _fallbackRate != 0 ? _fallbackRate : _worker.ObservedRateHz;
                }
            }
        }

        internal bool HasHealthyRun
        {
            get
            {
                lock (_gate)
                    return _healthyRun || (_monitoringRun && _worker.IsRunning && Stopwatch.GetElapsedTime(_runningSince).TotalSeconds >= 10);
            }
        }

        internal bool RestartRequired
        {
            get
            {
                lock (_gate)
                    return _cleanupStalled || _worker.StopReason == SmoothingStop.CleanupError;
            }
        }

        internal void Configure(bool enabled, int rateHz, int fallbackRate = 0, int delaySeconds = 0)
        {
            if (rateHz != 0 && (rateHz < 60 || rateHz > 1000))
                throw new ArgumentOutOfRangeException(nameof(rateHz));

            if (fallbackRate != 0 && (rateHz != 0 || fallbackRate < 60 || fallbackRate > 1000))
                throw new ArgumentOutOfRangeException(nameof(fallbackRate));

            if (delaySeconds < 0 || delaySeconds > 60)
                throw new ArgumentOutOfRangeException(nameof(delaySeconds));

            lock (_gate)
            {
                if (_quit || (_enabled == enabled && _rateHz == rateHz))
                    return;

                _enabled = enabled;
                _rateHz = rateHz;
                _fallbackRate = fallbackRate;
                _notBefore = delaySeconds == 0 ? 0 : Stopwatch.GetTimestamp() + Stopwatch.Frequency * delaySeconds;
                _healthyRun = false;
                _lastReason = SmoothingStop.None;
                _version++;
                _ = _worker.StopAsync();
                if (_changed.CurrentCount == 0)
                    _changed.Release();

                _loop ??= Task.Run(RunAsync);
            }
        }

        internal Task ShutdownAsync()
        {
            lock (_gate)
            {
                _quit = true;
                _enabled = false;
                _version++;
                _ = _worker.StopAsync(SmoothingStop.Closed);
                if (_changed.CurrentCount == 0)
                    _changed.Release();

                return _loop ?? Task.CompletedTask;
            }
        }

        internal void SetEnvironmentReady(bool ready, bool reset = false)
        {
            lock (_gate)
            {
                if (_quit || (_environmentReady == ready && !reset))
                    return;

                _environmentReady = ready;
                _fallbackRate = 0;
                _version++;
                _ = _worker.StopAsync();
                if (_changed.CurrentCount == 0)
                    _changed.Release();
            }
        }

        internal SmoothingInterruption[] GetInterruptions()
        {
            lock (_gate)
            {
                int count = Math.Min(_interruptions, _history.Length);
                SmoothingInterruption[] result = new SmoothingInterruption[count];
                for (int i = 0; i < count; i++)
                    result[i] = _history[(_interruptions - count + i) % _history.Length];

                return result;
            }
        }

        internal void RecordRestartFailure(string detail)
        {
            lock (_gate)
                AppendInterruption(SmoothingStop.CleanupError, detail);
        }

        private void AppendInterruption(SmoothingStop reason, string? detail)
        {
            _history[_interruptions++ % _history.Length] = new SmoothingInterruption(DateTime.UtcNow, reason, detail);
        }

        private async Task RetireAsync()
        {
            Task cleanup = _worker.StopAsync();
            if (!cleanup.IsCompleted && await Task.WhenAny(cleanup, Task.Delay(1000)).ConfigureAwait(false) != cleanup && !cleanup.IsCompleted)
            {
                lock (_gate)
                {
                    _cleanupStalled = true;
                    AppendInterruption(SmoothingStop.CleanupError, $"Worker cleanup exceeded one second after {_worker.StopReason}.");
                }
            }

            // Escalate to process restart, but never abandon native ownership inside this process.
            await cleanup.ConfigureAwait(false);
            lock (_gate)
                _cleanupStalled = false;
        }

        private async Task RunAsync()
        {
            int failures = 0;
            while (true)
            {
                bool enabled;
                int version;
                int rate;
                int recoveryDelay;
                lock (_gate)
                {
                    if (_quit)
                        return;

                    enabled = _enabled && _environmentReady;
                    version = _version;
                    rate = _rateHz == 0 ? _fallbackRate : _rateHz;
                    recoveryDelay = (int)Math.Clamp(Math.Ceiling((_notBefore - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency), 0, 60000);
                }

                // Never overlap workers, including when native cleanup has not completed yet.
                await RetireAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (_quit)
                        return;

                    if (version != _version)
                        continue;
                }

                if (!enabled)
                {
                    failures = 0;
                    await _changed.WaitAsync().ConfigureAwait(false);
                    continue;
                }

                if (RestartRequired)
                {
                    await _changed.WaitAsync(1000).ConfigureAwait(false);
                    continue;
                }

                if (recoveryDelay > 0)
                {
                    await _changed.WaitAsync(recoveryDelay).ConfigureAwait(false);
                    continue;
                }

                Task start;
                lock (_gate)
                {
                    if (_quit || version != _version)
                        continue;

                    start = _worker.StartAsync(rate);
                }
                await start.ConfigureAwait(false);

                bool wasRunning = _worker.IsRunning;
                long runningSince = Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    _runningSince = runningSince;
                    _monitoringRun = wasRunning;
                }

                while (_worker.IsRunning)
                {
                    lock (_gate)
                    {
                        if (_quit || version != _version)
                            break;
                    }
                    await _changed.WaitAsync(100).ConfigureAwait(false);
                }

                bool resetBackoff = wasRunning && Stopwatch.GetElapsedTime(runningSince).TotalSeconds >= 10;
                lock (_gate)
                {
                    _monitoringRun = false;
                    _healthyRun |= resetBackoff;
                }

                SmoothingStop reason = _worker.StopReason;
                await RetireAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (_quit)
                        return;

                    if (version != _version)
                    {
                        failures = 0;
                        continue;
                    }

                    if (reason != SmoothingStop.DuplicateOwner || reason != _lastReason)
                        AppendInterruption(reason, _worker.Error);

                    _lastReason = reason;
                    if (_rateHz == 0 && _fallbackRate == 0 && (reason == SmoothingStop.ClockError || reason == SmoothingStop.ClockStall ||
                        reason == SmoothingStop.StartupTimeout || reason == SmoothingStop.Unavailable))
                        _fallbackRate = _worker.ObservedRateHz;

                    if (reason == SmoothingStop.DisplayChanged || reason == SmoothingStop.SessionChanged ||
                        reason == SmoothingStop.Suspended || reason == SmoothingStop.DisplayOff)
                        _fallbackRate = 0;
                }

                if (resetBackoff)
                    failures = 0;

                int delay = Math.Min(2000, 250 * (1 << Math.Min(failures++, 3)));
                await _changed.WaitAsync(delay).ConfigureAwait(false);
            }
        }
    }
}
