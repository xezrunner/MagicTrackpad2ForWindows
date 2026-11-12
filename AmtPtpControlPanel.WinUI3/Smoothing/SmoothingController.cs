using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    internal readonly record struct SmoothingInterruption(DateTime Time, SmoothingStop Reason, string? Detail);

    /// <summary>
    /// Keeps smoothing in the requested state, starting a new run after interruptions.
    /// </summary>
    internal sealed class SmoothingController
    {
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _changed = new SemaphoreSlim(0, 1);
        private readonly Queue<SmoothingInterruption> _history = new Queue<SmoothingInterruption>();
        private PointerSmoothing? _run;
        private Task? _loop;

        private bool _enabled;
        private bool _quit;
        private bool _cleanupStalled;
        private bool _environmentReady = true;
        private int _rateHz;
        private int _version;
        private int _fallbackRate;
        private int _restartAttempt;
        private SmoothingStop _lastReason;
        private long _notBefore;

        internal bool Enabled
        {
            get
            {
                lock (_gate)
                    return _enabled;
            }
        }

        internal bool IsRunning => _run?.Running == true;

        internal bool IsStopping => _run?.IsStopping == true;

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

        // True after a failed run, and while a restarted process waits out its recovery delay.
        internal bool HasFailed
        {
            get
            {
                lock (_gate)
                    return _lastReason != SmoothingStop.None || Stopwatch.GetTimestamp() < _notBefore;
            }
        }

        internal bool RestartRequired
        {
            get
            {
                lock (_gate)
                    return _cleanupStalled || _run?.Reason == SmoothingStop.CleanupError;
            }
        }

        internal void Configure(bool enabled, int rateHz, SmoothingRecovery? recovery = null)
        {
            lock (_gate)
            {
                if (_quit || (_enabled == enabled && _rateHz == rateHz))
                    return;

                _enabled = enabled;
                _rateHz = rateHz;
                _fallbackRate = 0;
                _notBefore = 0;
                if (recovery.HasValue)
                {
                    _fallbackRate = recovery.Value.FallbackHz;
                    _notBefore = Stopwatch.GetTimestamp() + Stopwatch.Frequency * recovery.Value.DelaySeconds;
                    _restartAttempt = recovery.Value.Attempt;
                }

                _lastReason = SmoothingStop.None;
                _version++;
                _run?.Stop(SmoothingStop.Disabled);
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
                _run?.Stop(SmoothingStop.Closed);
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
                _run?.Stop(SmoothingStop.Disabled);
                if (_changed.CurrentCount == 0)
                    _changed.Release();
            }
        }

        internal SmoothingInterruption[] GetInterruptions()
        {
            lock (_gate)
                return _history.ToArray();
        }

        // The next process starts with the fallback timer when the compositor clock is in use, because a stuck
        // compositor wait usually caused the restart.
        internal SmoothingRecovery CreateRecovery()
        {
            lock (_gate)
            {
                int fallback = _fallbackRate != 0 ? _fallbackRate : _run?.ObservedRateHz ?? 60;
                return SmoothingRecovery.ForRestart(_rateHz, fallback, _restartAttempt);
            }
        }

        internal void RecordRestartFailure(string detail)
        {
            lock (_gate)
                AppendInterruption(SmoothingStop.CleanupError, detail);
        }

        private void AppendInterruption(SmoothingStop reason, string? detail)
        {
            if (_history.Count == 32)
                _history.Dequeue();

            _history.Enqueue(new SmoothingInterruption(DateTime.UtcNow, reason, detail));
        }

        private async Task RetireAsync()
        {
            PointerSmoothing? run = _run;
            if (run == null)
                return;

            run.Stop(SmoothingStop.Disabled);
            Task cleanup = run.Done;
            if (!cleanup.IsCompleted && await Task.WhenAny(cleanup, Task.Delay(1000)).ConfigureAwait(false) != cleanup && !cleanup.IsCompleted)
            {
                lock (_gate)
                {
                    _cleanupStalled = true;
                    AppendInterruption(SmoothingStop.CleanupError, $"Worker cleanup exceeded one second after {run.Reason}.");
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

                // Never overlap runs, including when native cleanup has not completed yet.
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

                PointerSmoothing run;
                Task start;
                lock (_gate)
                {
                    if (_quit || version != _version)
                        continue;

                    run = new PointerSmoothing(rate);
                    _run = run;
                    start = run.StartAsync();
                }
                await start.ConfigureAwait(false);

                bool wasRunning = run.Running;
                long runningSince = Stopwatch.GetTimestamp();
                while (run.Running)
                {
                    lock (_gate)
                    {
                        if (_quit || version != _version)
                            break;
                    }
                    await _changed.WaitAsync(100).ConfigureAwait(false);
                }

                // Ten healthy seconds end both the retry backoff and the process-restart streak.
                bool resetBackoff = wasRunning && Stopwatch.GetElapsedTime(runningSince).TotalSeconds >= 10;
                if (resetBackoff)
                {
                    failures = 0;
                    lock (_gate)
                        _restartAttempt = 0;
                }

                SmoothingStop reason = run.Reason;
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
                        AppendInterruption(reason, run.Error);

                    _lastReason = reason;
                    if (_rateHz == 0 && _fallbackRate == 0 && (reason == SmoothingStop.ClockError || reason == SmoothingStop.ClockStall ||
                        reason == SmoothingStop.StartupTimeout || reason == SmoothingStop.Unavailable))
                        _fallbackRate = run.ObservedRateHz;

                    if (reason == SmoothingStop.DisplayChanged || reason == SmoothingStop.SessionChanged ||
                        reason == SmoothingStop.Suspended || reason == SmoothingStop.DisplayOff)
                        _fallbackRate = 0;
                }

                int delay = Math.Min(2000, 250 * (1 << Math.Min(failures++, 3)));
                await _changed.WaitAsync(delay).ConfigureAwait(false);
            }
        }
    }
}
