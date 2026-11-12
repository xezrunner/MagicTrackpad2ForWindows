using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    internal enum SmoothingStop
    {
        None,
        Disabled,
        Closed,
        Unavailable,
        DuplicateOwner,
        StartupTimeout,
        ServiceStall,
        ClockStall,
        ClockError,
        HookError,
        HookLost,
        OutputError,
        OutputTimeout,
        OutputMismatch,
        QueueOverflow,
        SessionChanged,
        Suspended,
        DisplayChanged,
        DisplayOff,
        NativeError,
        CleanupError
    }

    /// <summary>
    /// One smoothing run. Replaces processed Magic Trackpad 2 pointer movement with display-timed absolute cursor updates.
    /// </summary>
    internal sealed class PointerSmoothing
    {
        internal string? Error;

        private readonly TaskCompletionSource _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reason;
        private int _running;
        private long _estimatedPeriod;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Native.HookProc _hookProc;
        private readonly Native.WindowProc _windowProc;
        private Thread? _clock;

        private nint _hook;
        private nint _window;
        private nint _power;
        private nint _owner;
        private nint _library;
        private nint _timer;
        private readonly int _rateHz;
        private bool _sessionNotifications;
        private bool _classRegistered;
        private string? _className;
        private Native.CompositorWait? _wait;

        private long _latest;
        private long _lastClock;
        private long _lastService;
        private ProcessedMotion? _motion;
        private DesktopBounds _bounds;

        private bool _pending;
        private bool _observed;
        private Pixel _target;
        private Pixel _observedPosition;
        private Pixel _confirmed;
        private Pixel _submittedFrom;
        private long _issued;
        private long _observedAt;
        private nuint _pendingTag;
        private long _yieldUntil;
        private bool _rebaseAfterYield;

        private bool _rawInput;
        private bool _mouseActive;
        private long _trackpadAt;
        private long _mouseAt;
        private long _hookAt;
        private int _unhookedMoves;
        private Pixel _unhookedCursor;
        private readonly Dictionary<nint, bool> _trackpads = new Dictionary<nint, bool>();

        private static readonly long StallTicks = Stopwatch.Frequency / 20;
        private static readonly long DeviceHoldTicks = Stopwatch.Frequency * 3 / 20;
        private static readonly long HookLossTicks = Stopwatch.Frequency / 10;

        internal PointerSmoothing(int rateHz)
        {
            _rateHz = rateHz;
            _hookProc = Hook;
            _windowProc = WindowMessage;
        }

        internal Task Done => _done.Task;

        internal bool Running => Volatile.Read(ref _running) != 0 && Reason == SmoothingStop.None;

        internal bool IsStopping => !_done.Task.IsCompleted && Reason != SmoothingStop.None;

        internal SmoothingStop Reason => (SmoothingStop)Volatile.Read(ref _reason);

        internal int ObservedRateHz
        {
            get
            {
                long period = Volatile.Read(ref _estimatedPeriod);
                if (period <= 0)
                    return 60;

                return Math.Clamp((int)Math.Round(Stopwatch.Frequency / (double)period), 60, 1000);
            }
        }

        internal async Task StartAsync()
        {
            new Thread(Run) { IsBackground = true, Name = "Pointer smoothing hook" }.Start();
            if (await Task.WhenAny(_ready.Task, Task.Delay(2000)) != _ready.Task)
                Stop(SmoothingStop.StartupTimeout);
        }

        internal void Stop(SmoothingStop reason, string? detail = null)
        {
            if (Interlocked.CompareExchange(ref _reason, (int)reason, 0) == 0 && detail != null)
                Error = detail;

            _stop.Set();
        }

        private static void Check(bool success)
        {
            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static Pixel Cursor()
        {
            Check(Native.GetPhysicalCursorPos(out Native.Point point));
            return new Pixel(point.X, point.Y);
        }

        private void InitializeWindow()
        {
            _className = "MT2FWSmoothing." + Guid.NewGuid().ToString("N");
            Native.WindowClass windowClass = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Proc = _windowProc,
                Instance = Native.GetModuleHandleW(null),
                Name = _className
            };
            Check(Native.RegisterClassExW(ref windowClass) != 0);
            _classRegistered = true;

            // A hidden top-level window receives display/power broadcasts; message-only windows do not.
            _window = Native.CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, 0, 0, windowClass.Instance, 0);
            Check(_window != 0);
            Check(Native.WTSRegisterSessionNotification(_window, 0));
            _sessionNotifications = true;
            _power = Native.RegisterPowerSettingNotification(_window, Native.DisplayStatus, 0);
            Check(_power != 0);
            Check(Native.RegisterRawInputDevices(RawInputDevices(Native.RawInputSink, _window), 2, (uint)Marshal.SizeOf<Native.RawInputDevice>()));
            _rawInput = true;

            nint desktop = Native.OpenInputDesktop(0, false, 1);
            Check(desktop != 0);
            try
            {
                char[] name = new char[256];
                Check(Native.GetUserObjectInformationW(desktop, 2, name, (uint)(name.Length * 2), out _));
                string desktopName = new string(name).TrimEnd('\0');
                if (!string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase))
                    Stop(SmoothingStop.SessionChanged);
            }
            finally
            {
                Native.CloseDesktop(desktop);
            }
        }

        private static Native.RawInputDevice[] RawInputDevices(uint flags, nint target)
        {
            return new Native.RawInputDevice[]
            {
                new Native.RawInputDevice { UsagePage = Native.DigitizerPage, Usage = Native.TouchpadUsage, Flags = flags, Target = target },
                new Native.RawInputDevice { UsagePage = Native.DesktopPage, Usage = Native.MouseUsage, Flags = flags, Target = target }
            };
        }

        private void ReadRawInput(nint input)
        {
            uint size = Native.RawHeaderSize;
            if (Native.GetRawInputHeader(input, Native.RawInputHeaderCommand, out Native.RawHeader header, ref size, Native.RawHeaderSize) != Native.RawHeaderSize)
                return;

            long now = Stopwatch.GetTimestamp();
            if (header.Type == Native.RawHidType)
            {
                if (IsTrackpad(header.Device))
                    _trackpadAt = now;
                return;
            }

            size = Native.RawMouseSize;
            if (header.Type != Native.RawMouseType ||
                Native.GetRawMouse(input, Native.RawInputDataCommand, out Native.RawMouse mouse, ref size, Native.RawHeaderSize) != Native.RawMouseSize ||
                (mouse.X == 0 && mouse.Y == 0))
                return;

            // Windows reports PTP-generated and injected movement without a device handle. MT2FW exposes no mouse collection.
            if (header.Device != 0)
            {
                // Yield at once: the rebase drops motion that this mouse's first hook callback queued.
                _mouseAt = now;
                _pending = false;
                _rebaseAfterYield = true;
            }

            // Windows silently removes a hook whose callback times out. All raw mouse movement also reaches the hook,
            // so movement that keeps moving the cursor without callbacks means the hook is gone. SetCursorPos does neither.
            if (Running && now - _hookAt > HookLossTicks)
            {
                Pixel cursor = Cursor();
                if (cursor != _unhookedCursor && ++_unhookedMoves >= 3)
                    Stop(SmoothingStop.HookLost);

                _unhookedCursor = cursor;
            }
        }

        private bool IsTrackpad(nint device)
        {
            if (_trackpads.TryGetValue(device, out bool trackpad))
                return trackpad;

            Native.RawDeviceInfo info = new Native.RawDeviceInfo { Size = Native.RawDeviceInfoSize };
            uint size = Native.RawDeviceInfoSize;
            if (Native.GetRawInputDeviceInfoW(device, Native.RawDeviceInfoCommand, ref info, ref size) != Native.RawDeviceInfoSize)
                return false;

            trackpad = info.Type == Native.RawHidType && info.VendorId == Native.TrackpadVendor;
            _trackpads[device] = trackpad;
            return trackpad;
        }

        // The hook carries no device identity. Hook callbacks can run before the matching WM_INPUT, so recent
        // reports decide: smooth while the MT2 reports and no other mouse has moved.
        private bool TrackpadInUse(long now)
        {
            return _trackpadAt != 0 && now - _trackpadAt <= DeviceHoldTicks && (_mouseAt == 0 || now - _mouseAt > DeviceHoldTicks);
        }

        private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam)
        {
            try
            {
                if (message == Native.RawInput)
                    ReadRawInput(lParam);

                if (message == Native.DisplayChange)
                    Stop(SmoothingStop.DisplayChanged);

                if (message == Native.SessionChange && (wParam == 2 || wParam == 4 || wParam == 6 || wParam == 7))
                    Stop(SmoothingStop.SessionChanged);

                if (message == Native.PowerBroadcast)
                {
                    if (wParam == Native.PowerSuspend)
                        Stop(SmoothingStop.Suspended);

                    if (wParam == Native.PowerSettingChange && lParam != 0 &&
                        Marshal.PtrToStructure<Guid>(lParam) == Native.DisplayStatus &&
                        Marshal.ReadInt32(lParam, 16) == 4 && Marshal.ReadInt32(lParam, 20) == 0)
                        Stop(SmoothingStop.DisplayOff);
                }
            }
            catch (Exception)
            {
                Stop(SmoothingStop.NativeError);
            }
            return Native.DefWindowProcW(window, message, wParam, lParam);
        }

        private bool Healthy(long now)
        {
            // StartAsync bounds startup separately. Until admission begins, no movement can become stale.
            if (Volatile.Read(ref _running) == 0)
                return Reason == SmoothingStop.None;

            long serviceGap = now - Volatile.Read(ref _lastService);
            long clockGap = now - Volatile.Read(ref _lastClock);
            if (serviceGap > StallTicks)
                Stop(SmoothingStop.ServiceStall, $"Owner gap: {serviceGap * 1000.0 / Stopwatch.Frequency:F2} ms");

            if (clockGap > StallTicks)
                Stop(SmoothingStop.ClockStall, $"Clock gap: {clockGap * 1000.0 / Stopwatch.Frequency:F2} ms");

            return Reason == SmoothingStop.None;
        }

        private void ClockPump()
        {
            nint[] handles = _rateHz == 0
                ? new nint[] { _stop.SafeWaitHandle.DangerousGetHandle() }
                : new nint[] { _stop.SafeWaitHandle.DangerousGetHandle(), _timer };
            long period = _rateHz == 0 ? 0 : (long)Math.Round(Stopwatch.Frequency / (double)_rateHz);
            long deadline = Stopwatch.GetTimestamp();
            try
            {
                while (Reason == SmoothingStop.None)
                {
                    uint result;
                    if (_rateHz == 0)
                    {
                        result = _wait!(1, handles, 10);
                    }
                    else
                    {
                        long remaining = Math.Max(1, deadline - Stopwatch.GetTimestamp());
                        long due = -Math.Max(1, (long)Math.Ceiling(remaining * (10_000_000.0 / Stopwatch.Frequency)));
                        Check(Native.SetWaitableTimerEx(_timer, in due, 0, 0, 0, 0, 0));
                        result = Native.WaitForMultipleObjects(2, handles, false, 10);
                    }

                    if (Reason != SmoothingStop.None)
                        break;

                    long now = Stopwatch.GetTimestamp();
                    if (!Healthy(now))
                        break;

                    if (result == Native.WaitTimeout)
                        continue;

                    // Stop = 0, compositor tick = count (1), fixed timer tick = index 1.
                    if (result != 1)
                    {
                        Stop(SmoothingStop.ClockError, $"Clock result: 0x{result:X8}");
                        break;
                    }

                    // Skip missed deadlines instead of replaying a burst after scheduling delays.
                    if (_rateHz != 0)
                        deadline += Math.Max(1, (now - deadline) / period + 1) * period;

                    long gap = now - Volatile.Read(ref _lastClock);
                    if (_rateHz == 0 && gap > 0 && gap < StallTicks)
                    {
                        long estimate = Volatile.Read(ref _estimatedPeriod);
                        Volatile.Write(ref _estimatedPeriod, estimate == 0 ? gap : (7 * estimate + gap) / 8);
                    }

                    Volatile.Write(ref _lastClock, now);
                    Interlocked.Exchange(ref _latest, now);
                    _wake.Set();
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Stop(SmoothingStop.ClockError);
            }
        }

        private void DrainMessages()
        {
            for (int i = 0; i < 128; i++)
            {
                if (!Native.PeekMessageW(out Native.Message message, 0, 0, 0, Native.RemoveMessage))
                    break;

                Native.DispatchMessageW(ref message);
            }
        }

        private nint Hook(int code, nuint message, nint data)
        {
            if (code == 0)
            {
                try
                {
                    _hookAt = Stopwatch.GetTimestamp();
                    _unhookedMoves = 0;
                    Native.MouseHook input = Marshal.PtrToStructure<Native.MouseHook>(data);
                    if ((input.Flags & Native.Injected) != 0 && Native.IsSmoothingInput(input.Extra))
                    {
                        // A retired output must not acknowledge a newer replacement or become fresh movement.
                        if (message == Native.MouseMove && (!Running || !_pending || input.Extra != _pendingTag))
                            return 1;

                        if (_pending)
                        {
                            _observed = true;
                            _observedPosition = new Pixel(input.Point.X, input.Point.Y);
                            _observedAt = Stopwatch.GetTimestamp();
                        }
                    }
                    else if ((input.Flags & Native.Injected) != 0 && message == Native.MouseMove)
                    {
                        _pending = false;
                        _yieldUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                        _rebaseAfterYield = true;
                    }
                    else if (Running && Healthy(Stopwatch.GetTimestamp()))
                    {
                        long stamp = Stopwatch.GetTimestamp();
                        if (stamp < _yieldUntil)
                            return Native.CallNextHookEx(0, code, message, data);

                        if (!TrackpadInUse(stamp))
                        {
                            // Another mouse owns the cursor. Retire any replacement and resume from its position later.
                            if (message == Native.MouseMove)
                            {
                                _pending = false;
                                _rebaseAfterYield = true;
                                _mouseActive = true;
                            }
                            return Native.CallNextHookEx(0, code, message, data);
                        }

                        _mouseActive = false;
                        if (_rebaseAfterYield)
                        {
                            _confirmed = Cursor();
                            _motion!.Rebase(_confirmed);
                            _rebaseAfterYield = false;
                        }

                        if (message == Native.MouseMove)
                        {
                            // Read the actual cursor even during reentrant SendInput, as in the validated worker.
                            Pixel before = Cursor();
                            _motion!.Add(stamp, new Pair((double)input.Point.X - before.X, (double)input.Point.Y - before.Y), input.Time);
                            if (Reason == SmoothingStop.None)
                                return 1;
                        }
                        else if (message == Native.LeftDown || message == Native.LeftUp || message == Native.RightDown || message == Native.RightUp ||
                            message == Native.MiddleDown || message == Native.MiddleUp || message == Native.XDown || message == Native.XUp)
                        {
                            _motion!.Add(stamp, default, input.Time, true);
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    Stop(SmoothingStop.QueueOverflow);
                }
                catch (Exception)
                {
                    Stop(SmoothingStop.HookError);
                }
            }
            return Native.CallNextHookEx(0, code, message, data);
        }

        private void CheckReplacement(Pixel cursor, long now)
        {
            if (!_pending)
            {
                if (cursor != _confirmed)
                {
                    _motion!.Rebase(cursor);
                    _confirmed = cursor;
                }
                return;
            }

            bool expired = now - _issued > StallTicks;
            if (expired && (!_observed || _observedAt - _issued > StallTicks))
            {
                Stop(SmoothingStop.OutputTimeout);
                return;
            }

            if (expired && cursor == _submittedFrom)
            {
                // Seeing our tag precedes downstream hooks; it does not prove that Windows moved the cursor.
                Check(Native.GetClipCursor(out Native.Rect clip));
                if (_target.X >= clip.Left && _target.X < clip.Right && _target.Y >= clip.Top && _target.Y < clip.Bottom &&
                    Native.MonitorFromPoint(new Native.Point { X = _target.X, Y = _target.Y }, Native.MonitorDefaultToNull) != 0)
                {
                    Stop(SmoothingStop.OutputMismatch);
                    return;
                }
            }

            if (_observed && (expired || cursor == _observedPosition || cursor != _submittedFrom))
            {
                // Correct the submitted position without discarding motion along a constrained edge.
                if (cursor != _target)
                    _motion!.CorrectPosition(_target, cursor);

                _confirmed = cursor;
                _pending = false;
            }
        }

        private void Run()
        {
            try
            {
                _owner = Native.CreateMutexW(0, false, @"Local\MT2FWPointerSmoothing.controller");
                int error = Marshal.GetLastWin32Error();
                if (_owner == 0 || error == Native.ErrorAlreadyExists)
                {
                    Stop(SmoothingStop.DuplicateOwner);
                    throw new Win32Exception(error);
                }

                if (_rateHz == 0)
                {
                    if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "dcomp.dll"), out _library) ||
                        !NativeLibrary.TryGetExport(_library, "DCompositionWaitForCompositorClock", out nint export))
                    {
                        Stop(SmoothingStop.Unavailable);
                        return;
                    }
                    _wait = Marshal.GetDelegateForFunctionPointer<Native.CompositorWait>(export);
                }
                else
                {
                    _timer = Native.CreateWaitableTimerExW(0, null, Native.HighResolutionTimer, Native.TimerAccess);
                    Check(_timer != 0);
                }

                InitializeWindow();
                _bounds = new DesktopBounds(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));

                long start = Stopwatch.GetTimestamp();
                _lastClock = _lastService = start;
                if (Reason != SmoothingStop.None)
                    return;

                _clock = new Thread(ClockPump) { IsBackground = true, Name = "Pointer smoothing clock" };
                _clock.Start();
                nint[] handles = new nint[] { _stop.SafeWaitHandle.DangerousGetHandle(), _wake.SafeWaitHandle.DangerousGetHandle() };

                // Validate at least one clock result and process initial notifications before hooking.
                while (Reason == SmoothingStop.None && Interlocked.Read(ref _latest) == 0)
                {
                    Volatile.Write(ref _lastService, Stopwatch.GetTimestamp());
                    uint result = Native.MsgWaitForMultipleObjectsEx(2, handles, 10, Native.AllInput, Native.InputAvailable);
                    if (result != 0 && result != 1 && result != 2 && result != Native.WaitTimeout)
                        Stop(SmoothingStop.NativeError);

                    DrainMessages();
                    Healthy(Stopwatch.GetTimestamp());
                }

                DrainMessages();
                if (Reason != SmoothingStop.None)
                    return;

                _hook = Native.SetWindowsHookExW(Native.MouseLowLevelHook, _hookProc, Native.GetModuleHandleW(null), 0);
                Check(_hook != 0);

                // Movement passes through until the hook and clock are ready; adopt that final native position.
                _confirmed = Cursor();
                start = Stopwatch.GetTimestamp();
                _hookAt = start;
                _motion = new ProcessedMotion(new Pair(_confirmed.X, _confirmed.Y), _bounds, start, Stopwatch.Frequency);
                Volatile.Write(ref _lastService, Stopwatch.GetTimestamp());
                if (!Healthy(start))
                    return;

                Volatile.Write(ref _running, 1);
                _ready.TrySetResult();
                Native.Input packet = new Native.Input { Mouse = new Native.MouseInput { Flags = Native.AbsoluteMove, Extra = Native.InputTag } };
                while (Reason == SmoothingStop.None)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (!Healthy(now))
                        break;

                    Volatile.Write(ref _lastService, now);
                    Pixel cursor = Cursor();
                    CheckReplacement(cursor, Stopwatch.GetTimestamp());
                    long tick = Interlocked.Exchange(ref _latest, 0);
                    DrainMessages();
                    now = Stopwatch.GetTimestamp();
                    if (!Healthy(now))
                        break;

                    if (tick != 0)
                    {
                        if (now - tick > StallTicks)
                        {
                            Stop(SmoothingStop.ClockStall);
                            break;
                        }

                        if (now < _yieldUntil || _mouseActive)
                            continue;

                        Pixel before = Cursor();
                        if (_rebaseAfterYield)
                        {
                            _motion.Rebase(before);
                            _confirmed = before;
                            _rebaseAfterYield = false;
                        }

                        CheckReplacement(before, Stopwatch.GetTimestamp());
                        if (Reason != SmoothingStop.None)
                            break;

                        Pixel target = _motion.Sample(now);
                        if (!_pending && target != before && Healthy(Stopwatch.GetTimestamp()))
                        {
                            packet.Mouse.X = Native.Normalize(target.X, _bounds.X, _bounds.Width);
                            packet.Mouse.Y = Native.Normalize(target.Y, _bounds.Y, _bounds.Height);
                            packet.Mouse.Extra = _pendingTag = Native.NextInputTag();
                            _target = target;
                            _submittedFrom = before;
                            _issued = now;
                            _pending = true;
                            _observed = false;
                            if (Native.SendInput(1, ref packet, Marshal.SizeOf<Native.Input>()) != 1)
                                Stop(SmoothingStop.OutputError);
                        }
                    }

                    if (Reason != SmoothingStop.None)
                        break;

                    uint result = Native.MsgWaitForMultipleObjectsEx(2, handles, 10, Native.AllInput, Native.InputAvailable);
                    if (result != 0 && result != 1 && result != 2 && result != Native.WaitTimeout)
                        Stop(SmoothingStop.NativeError);
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Stop(SmoothingStop.NativeError);
            }
            finally
            {
                Stop(SmoothingStop.Disabled);
                Volatile.Write(ref _running, 0);
                _ready.TrySetResult();

                // Failed cleanup keeps the callbacks rooted and the ownership mutex claimed, so no other run can start.
                bool retained = false;
                if (_hook != 0 && !Native.UnhookWindowsHookEx(_hook))
                {
                    // A hook that Windows already removed after a callback timeout has no live handle left.
                    int error = Marshal.GetLastWin32Error();
                    if (error != Native.ErrorInvalidHookHandle)
                    {
                        Error = "UnhookWindowsHookEx failed: " + error;
                        retained = true;
                    }
                }

                _clock?.Join();
                if (_timer != 0 && !Native.CloseHandle(_timer))
                    retained = true;

                if (_rawInput)
                    Native.RegisterRawInputDevices(RawInputDevices(Native.RawInputRemove, 0), 2, (uint)Marshal.SizeOf<Native.RawInputDevice>());

                if (_power != 0)
                    Native.UnregisterPowerSettingNotification(_power);

                if (_sessionNotifications)
                    Native.WTSUnRegisterSessionNotification(_window);

                if (_window != 0 && !Native.DestroyWindow(_window))
                    retained = true;
                else if (_classRegistered && !Native.UnregisterClassW(_className!, Native.GetModuleHandleW(null)))
                    retained = true;

                _motion = null;
                if (_library != 0)
                    NativeLibrary.Free(_library);

                if (retained)
                {
                    Error = $"Cleanup failed after {Reason}. {Error}";
                    Interlocked.Exchange(ref _reason, (int)SmoothingStop.CleanupError);
                }
                else if (_owner != 0)
                {
                    Native.CloseHandle(_owner);
                }

                _done.TrySetResult();
            }
        }
    }
}
