using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    internal enum SmoothingStop
    {
        None, Disabled, Closed, Unavailable, DuplicateOwner, StartupTimeout, ServiceStall, ClockStall,
        ClockError, HookError, OutputError, OutputTimeout, OutputMismatch, QueueOverflow,
        SessionChanged, Suspended, DisplayChanged, DisplayOff, NativeError, CleanupError, ExternalMovement
    }

    internal sealed class PointerSmoothing
    {
        private readonly object _gate = new object();
        private Session? _session;
        internal bool IsRunning => _session?.Running == true;
        internal bool IsStopping => _session != null && !_session.Done.Task.IsCompleted && _session.Reason != SmoothingStop.None;
        internal SmoothingStop StopReason => _session?.Reason ?? SmoothingStop.None;
        internal string? Error => _session?.Error;
        internal int ObservedRateHz
        {
            get
            {
                long period = _session == null ? 0 : Volatile.Read(ref _session.EstimatedPeriod);
                return period > 0 ? Math.Clamp((int)Math.Round(Stopwatch.Frequency / (double)period), 60, 1000) : 60;
            }
        }

        internal async Task StartAsync(int rateHz = 0)
        {
            if (rateHz != 0 && (rateHz < 60 || rateHz > 1000)) throw new ArgumentOutOfRangeException(nameof(rateHz));
            Session session;
            lock (_gate)
            {
                if (_session != null && (!_session.Done.Task.IsCompleted || _session.Retained)) return;
                session = new Session(rateHz);
                _session = session;
                session.Start();
            }
            if (await Task.WhenAny(session.Ready.Task, Task.Delay(2000)) != session.Ready.Task)
                session.Stop(SmoothingStop.StartupTimeout);
        }

        internal Task StopAsync(SmoothingStop reason = SmoothingStop.Disabled)
        {
            lock (_gate)
            {
                if (_session == null) return Task.CompletedTask;
                _session.Stop(reason);
                return _session.Done.Task;
            }
        }

        internal static bool IsAvailable()
        {
            if (IntPtr.Size != 8) return false;
            if (NativeLibrary.TryLoad(System.IO.Path.Combine(Environment.SystemDirectory, "dcomp.dll"), out nint library))
            {
                try { if (NativeLibrary.TryGetExport(library, "DCompositionWaitForCompositorClock", out _)) return true; }
                finally { NativeLibrary.Free(library); }
            }
            nint timer = Native.CreateWaitableTimerExW(0, null, Native.HighResolutionTimer, Native.TimerAccess);
            return timer != 0 && Native.CloseHandle(timer);
        }

        // NOTE: Keep failed-unhook callbacks rooted for the remaining native lifetime and refuse another session.
        private static Session? _retained;

        private sealed class Session
        {
            internal readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Running => Volatile.Read(ref _running) != 0 && Reason == SmoothingStop.None;
            internal SmoothingStop Reason => (SmoothingStop)Volatile.Read(ref _reason);
            internal string? Error;
            internal bool Retained;
            internal long EstimatedPeriod;
            private int _reason, _running;
            private readonly object _signalGate = new object();
            private bool _finished;
            private readonly ManualResetEvent _stop = new ManualResetEvent(false);
            private readonly AutoResetEvent _wake = new AutoResetEvent(false);
            private readonly Native.HookProc _hookProc;
            private readonly Native.WindowProc _windowProc;
            private Thread? _clock;
            private bool _clockStarted;
            private nint _hook, _window, _power, _controller, _worker, _library, _timer;
            private readonly int _rateHz;
            private bool _sessionNotifications, _classRegistered;
            private string? _className;
            private Native.CompositorWait? _wait;
            private long _latest, _lastClock, _lastService;
            private ProcessedMotion? _motion;
            private DesktopBounds _bounds;
            private bool _pending, _observed;
            private Pixel _target, _observedPosition, _confirmed, _submittedFrom;
            private long _issued, _observedAt;
            private nuint _pendingTag;
            private long _yieldUntil;
            private bool _rebaseAfterYield;
            private bool _rawInput, _mouseActive;
            private long _trackpadAt, _mouseAt;
            private readonly Dictionary<nint, bool> _trackpads = new Dictionary<nint, bool>();
            private static readonly Guid DisplayStatus = new Guid("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
            private static readonly long StallTicks = Stopwatch.Frequency / 20;
            private static readonly long DeviceHoldTicks = Stopwatch.Frequency * 3 / 20;

            internal Session(int rateHz) { _rateHz = rateHz; _hookProc = Hook; _windowProc = WindowMessage; }
            internal void Start()
            {
                try { new Thread(Run) { IsBackground = true, Name = "Pointer smoothing hook" }.Start(); }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    Stop(SmoothingStop.NativeError);
                    lock (_signalGate) { _finished = true; _stop.Dispose(); _wake.Dispose(); }
                    Ready.TrySetResult(false);
                    Done.TrySetResult(true);
                }
            }

            internal void Stop(SmoothingStop reason, string? detail = null)
            {
                if (Interlocked.CompareExchange(ref _reason, (int)reason, 0) == 0 && detail != null) Error = detail;
                lock (_signalGate) { if (!_finished) _stop.Set(); }
            }

            private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            private static Pixel Cursor()
            {
                Check(Native.GetPhysicalCursorPos(out var point));
                return point.Pixel;
            }

            private nint Claim(string role)
            {
                nint handle = Native.CreateMutexW(0, false, @"Local\MT2FWPointerSmoothing." + role);
                int error = Marshal.GetLastWin32Error();
                if (handle == 0 || error == Native.ErrorAlreadyExists)
                {
                    if (handle != 0) Native.CloseHandle(handle);
                    Stop(SmoothingStop.DuplicateOwner);
                    throw new Win32Exception(error);
                }
                return handle;
            }

            private void InitializeWindow()
            {
                _className = "MT2FWSmoothing." + Guid.NewGuid().ToString("N");
                var windowClass = new Native.WindowClass
                {
                    Size = (uint)Marshal.SizeOf<Native.WindowClass>(), Proc = _windowProc,
                    Instance = Native.GetModuleHandleW(null), Name = _className
                };
                Check(Native.RegisterClassExW(ref windowClass) != 0);
                _classRegistered = true;
                // NOTE: A hidden top-level window receives display/power broadcasts; message-only windows do not.
                _window = Native.CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, 0, 0, windowClass.Instance, 0);
                Check(_window != 0);
                Check(Native.WTSRegisterSessionNotification(_window, 0));
                _sessionNotifications = true;
                _power = Native.RegisterPowerSettingNotification(_window, DisplayStatus, 0);
                Check(_power != 0);
                Check(Native.RegisterRawInputDevices(RawInputDevices(Native.RawInputSink, _window), 2, (uint)Marshal.SizeOf<Native.RawInputDevice>()));
                _rawInput = true;

                nint desktop = Native.OpenInputDesktop(0, false, 1);
                Check(desktop != 0);
                try
                {
                    var name = new char[256];
                    Check(Native.GetUserObjectInformationW(desktop, 2, name, (uint)(name.Length * 2), out _));
                    string desktopName = new string(name).TrimEnd('\0');
                    if (!string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase)) Stop(SmoothingStop.SessionChanged);
                }
                finally { Native.CloseDesktop(desktop); }
            }

            private static Native.RawInputDevice[] RawInputDevices(uint flags, nint target) => new[]
            {
                new Native.RawInputDevice { UsagePage = Native.DigitizerPage, Usage = Native.TouchpadUsage, Flags = flags, Target = target },
                new Native.RawInputDevice { UsagePage = Native.DesktopPage, Usage = Native.MouseUsage, Flags = flags, Target = target }
            };

            private void ReadRawInput(nint input)
            {
                uint size = Native.RawHeaderSize;
                if (Native.GetRawInputHeader(input, Native.RawInputHeaderCommand, out var header, ref size, Native.RawHeaderSize) != Native.RawHeaderSize) return;
                long now = Stopwatch.GetTimestamp();
                if (header.Type == Native.RawHidType)
                {
                    if (IsTrackpad(header.Device)) _trackpadAt = now;
                    return;
                }
                // NOTE: Windows reports PTP-generated and injected movement without a device handle. MT2FW exposes no mouse collection.
                if (header.Type != Native.RawMouseType || header.Device == 0) return;
                size = Native.RawMouseSize;
                if (Native.GetRawMouse(input, Native.RawInputDataCommand, out var mouse, ref size, Native.RawHeaderSize) == Native.RawMouseSize &&
                    (mouse.X != 0 || mouse.Y != 0))
                    _mouseAt = now;
            }

            private bool IsTrackpad(nint device)
            {
                if (_trackpads.TryGetValue(device, out bool trackpad)) return trackpad;
                var info = new Native.RawDeviceInfo { Size = Native.RawDeviceInfoSize };
                uint size = Native.RawDeviceInfoSize;
                if (Native.GetRawInputDeviceInfoW(device, Native.RawDeviceInfoCommand, ref info, ref size) != Native.RawDeviceInfoSize) return false;
                trackpad = info.Type == Native.RawHidType && info.VendorId == Native.TrackpadVendor;
                _trackpads[device] = trackpad;
                return trackpad;
            }

            // NOTE: The hook carries no device identity. Hook callbacks can run before the matching WM_INPUT, so recent
            // reports decide: smooth while the MT2 reports and no other mouse has moved.
            private bool TrackpadInUse(long now) =>
                _trackpadAt != 0 && now - _trackpadAt <= DeviceHoldTicks && (_mouseAt == 0 || now - _mouseAt > DeviceHoldTicks);

            private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam)
            {
                try
                {
                    if (message == Native.RawInput) ReadRawInput(lParam);
                    if (message == Native.DisplayChange) Stop(SmoothingStop.DisplayChanged);
                    if (message == Native.SessionChange && (wParam == 2 || wParam == 4 || wParam == 6 || wParam == 7)) Stop(SmoothingStop.SessionChanged);
                    if (message == Native.PowerBroadcast)
                    {
                        if (wParam == Native.PowerSuspend) Stop(SmoothingStop.Suspended);
                        if (wParam == Native.PowerSettingChange && lParam != 0 &&
                            Marshal.PtrToStructure<Guid>(lParam) == DisplayStatus &&
                            Marshal.ReadInt32(lParam, 16) == 4 && Marshal.ReadInt32(lParam, 20) == 0)
                            Stop(SmoothingStop.DisplayOff);
                    }
                }
                catch (Exception) { Stop(SmoothingStop.NativeError); }
                return Native.DefWindowProcW(window, message, wParam, lParam);
            }

            private bool Healthy(long now)
            {
                // NOTE: StartAsync bounds startup separately. Until admission begins, no movement can become stale.
                if (Volatile.Read(ref _running) == 0) return Reason == SmoothingStop.None;
                long serviceGap = now - Volatile.Read(ref _lastService), clockGap = now - Volatile.Read(ref _lastClock);
                if (serviceGap > StallTicks) Stop(SmoothingStop.ServiceStall, $"Owner gap: {serviceGap * 1000.0 / Stopwatch.Frequency:F2} ms");
                if (clockGap > StallTicks) Stop(SmoothingStop.ClockStall, $"Clock gap: {clockGap * 1000.0 / Stopwatch.Frequency:F2} ms");
                return Reason == SmoothingStop.None;
            }

            private void ClockPump()
            {
                var handles = _rateHz == 0 ? new[] { _stop.SafeWaitHandle.DangerousGetHandle() } :
                    new[] { _stop.SafeWaitHandle.DangerousGetHandle(), _timer };
                long period = _rateHz == 0 ? 0 : (long)Math.Round(Stopwatch.Frequency / (double)_rateHz);
                long deadline = Stopwatch.GetTimestamp();
                try
                {
                    while (Reason == SmoothingStop.None)
                    {
                        uint result;
                        if (_rateHz == 0) result = _wait!(1, handles, 10);
                        else
                        {
                            long remaining = Math.Max(1, deadline - Stopwatch.GetTimestamp());
                            long due = -Math.Max(1, (long)Math.Ceiling(remaining * (10_000_000.0 / Stopwatch.Frequency)));
                            Check(Native.SetWaitableTimerEx(_timer, in due, 0, 0, 0, 0, 0));
                            result = Native.WaitForMultipleObjects(2, handles, false, 10);
                        }
                        if (Reason != SmoothingStop.None) break;
                        long now = Stopwatch.GetTimestamp();
                        if (!Healthy(now)) break;
                        if (result == Native.WaitTimeout) continue;
                        // NOTE: Stop = 0, compositor tick = count (1), fixed timer tick = index 1.
                        if (result != 1) { Stop(SmoothingStop.ClockError, $"Clock result: 0x{result:X8}"); break; }
                        // NOTE: Skip missed deadlines instead of replaying a burst after scheduling delays.
                        if (_rateHz != 0) deadline += Math.Max(1, (now - deadline) / period + 1) * period;
                        long gap = now - Volatile.Read(ref _lastClock);
                        if (_rateHz == 0 && gap > 0 && gap < StallTicks)
                        {
                            long estimate = Volatile.Read(ref EstimatedPeriod);
                            Volatile.Write(ref EstimatedPeriod, estimate == 0 ? gap : (7 * estimate + gap) / 8);
                        }
                        Volatile.Write(ref _lastClock, now);
                        Interlocked.Exchange(ref _latest, now);
                        _wake.Set();
                    }
                }
                catch (Exception ex) { Error = ex.Message; Stop(SmoothingStop.ClockError); }
            }

            private void DrainMessages()
            {
                for (int i = 0; i < 128; i++)
                {
                    if (!Native.PeekMessageW(out var message, 0, 0, 0, Native.RemoveMessage)) break;
                    if (message.Id == Native.Quit) { Stop(SmoothingStop.Closed); break; }
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessageW(ref message);
                }
            }

            private nint Hook(int code, nuint message, nint data)
            {
                if (code == 0)
                {
                    try
                    {
                        var input = Marshal.PtrToStructure<Native.MouseHook>(data);
                        if ((input.Flags & Native.Injected) != 0 && Native.IsSmoothingInput(input.Extra))
                        {
                            // NOTE: A retired output must not acknowledge a newer replacement or become fresh movement.
                            if (message == Native.MouseMove && (!Running || !_pending || input.Extra != _pendingTag)) return 1;
                            if (_pending)
                            {
                                _observed = true; _observedPosition = input.Point.Pixel;
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
                            if (stamp < _yieldUntil) return Native.CallNextHookEx(0, code, message, data);
                            if (!TrackpadInUse(stamp))
                            {
                                // NOTE: Another mouse owns the cursor. Retire any replacement and resume from its position later.
                                if (message == Native.MouseMove) { _pending = false; _rebaseAfterYield = true; _mouseActive = true; }
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
                                // NOTE: Read the actual cursor even during reentrant SendInput, as in the validated worker.
                                Pixel before = Cursor();
                                _motion!.Add(stamp, new Pair((double)input.Point.X - before.X, (double)input.Point.Y - before.Y), input.Time);
                                if (Reason == SmoothingStop.None) return 1;
                            }
                            else if (message == Native.LeftDown || message == Native.LeftUp || message == Native.RightDown || message == Native.RightUp ||
                                message == Native.MiddleDown || message == Native.MiddleUp || message == Native.XDown || message == Native.XUp)
                                _motion!.Add(stamp, default, input.Time, true);
                        }
                    }
                    catch (InvalidOperationException) { Stop(SmoothingStop.QueueOverflow); }
                    catch (Exception) { Stop(SmoothingStop.HookError); }
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
                    // NOTE: Seeing our tag precedes downstream hooks; it does not prove that Windows moved the cursor.
                    Check(Native.GetClipCursor(out var clip));
                    if (_target.X >= clip.Left && _target.X < clip.Right && _target.Y >= clip.Top && _target.Y < clip.Bottom &&
                        Native.MonitorFromPoint(new Native.Point { X = _target.X, Y = _target.Y }, Native.MonitorDefaultToNull) != 0)
                    {
                        Stop(SmoothingStop.OutputMismatch);
                        return;
                    }
                }
                if (_observed && (expired || cursor == _observedPosition || cursor != _submittedFrom))
                {
                    // NOTE: Correct the submitted position without discarding motion along a constrained edge.
                    if (cursor != _target) _motion!.CorrectPosition(_target, cursor);
                    _confirmed = cursor;
                    _pending = false;
                }
            }

            private void Run()
            {
                try
                {
                    if (IntPtr.Size != 8 || Marshal.SizeOf<Native.Input>() != 40 || Marshal.SizeOf<Native.MouseHook>() != 32 ||
                        Marshal.SizeOf<Native.RawHeader>() != Native.RawHeaderSize || Marshal.SizeOf<Native.RawMouse>() != Native.RawMouseSize ||
                        Marshal.SizeOf<Native.RawDeviceInfo>() != Native.RawDeviceInfoSize)
                        throw new PlatformNotSupportedException("64-bit input layout required");
                    _controller = Claim("controller");
                    _worker = Claim("worker");
                    if (_rateHz == 0)
                    {
                        if (!NativeLibrary.TryLoad(System.IO.Path.Combine(Environment.SystemDirectory, "dcomp.dll"), out _library) ||
                            !NativeLibrary.TryGetExport(_library, "DCompositionWaitForCompositorClock", out nint export))
                        { Stop(SmoothingStop.Unavailable); return; }
                        _wait = Marshal.GetDelegateForFunctionPointer<Native.CompositorWait>(export);
                    }
                    else
                    {
                        _timer = Native.CreateWaitableTimerExW(0, null, Native.HighResolutionTimer, Native.TimerAccess);
                        Check(_timer != 0);
                    }
                    InitializeWindow();
                    _bounds = new DesktopBounds(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
                    if (_bounds.Width <= 0 || _bounds.Height <= 0 || _bounds.Width > 65536 || _bounds.Height > 65536)
                        throw new PlatformNotSupportedException("Unsupported virtual desktop bounds");
                    long start = Stopwatch.GetTimestamp();
                    _lastClock = _lastService = start;
                    if (Reason != SmoothingStop.None) return;
                    _clock = new Thread(ClockPump) { IsBackground = true, Name = "Pointer smoothing clock" };
                    _clock.Start();
                    _clockStarted = true;
                    var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle(), _wake.SafeWaitHandle.DangerousGetHandle() };
                    // NOTE: Validate at least one clock result and process initial notifications before hooking.
                    while (Reason == SmoothingStop.None && Interlocked.Read(ref _latest) == 0)
                    {
                        Volatile.Write(ref _lastService, Stopwatch.GetTimestamp());
                        uint result = Native.MsgWaitForMultipleObjectsEx(2, handles, 10, Native.AllInput, Native.InputAvailable);
                        if (result != 0 && result != 1 && result != 2 && result != Native.WaitTimeout) Stop(SmoothingStop.NativeError);
                        DrainMessages();
                        Healthy(Stopwatch.GetTimestamp());
                    }
                    DrainMessages();
                    if (Reason != SmoothingStop.None) return;
                    _hook = Native.SetWindowsHookExW(Native.MouseLowLevelHook, _hookProc, Native.GetModuleHandleW(null), 0);
                    Check(_hook != 0);
                    // NOTE: Movement passes through until the hook and clock are ready; adopt that final native position.
                    _confirmed = Cursor();
                    start = Stopwatch.GetTimestamp();
                    _motion = new ProcessedMotion(new Pair(_confirmed.X, _confirmed.Y), _bounds, start, Stopwatch.Frequency);
                    Volatile.Write(ref _lastService, Stopwatch.GetTimestamp());
                    if (!Healthy(start)) return;
                    Volatile.Write(ref _running, 1);
                    Ready.TrySetResult(Running);
                    var packet = new Native.Input { Mouse = new Native.MouseInput { Flags = Native.AbsoluteMove, Extra = Native.InputTag } };
                    while (Reason == SmoothingStop.None)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (!Healthy(now)) break;
                        Volatile.Write(ref _lastService, now);
                        Pixel cursor = Cursor();
                        CheckReplacement(cursor, Stopwatch.GetTimestamp());
                        long tick = Interlocked.Exchange(ref _latest, 0);
                        DrainMessages();
                        now = Stopwatch.GetTimestamp();
                        if (!Healthy(now)) break;
                        if (tick != 0)
                        {
                            if (now - tick > StallTicks) { Stop(SmoothingStop.ClockStall); break; }
                            if (now < _yieldUntil || _mouseActive) continue;
                            Pixel before = Cursor();
                            if (_rebaseAfterYield)
                            {
                                _motion.Rebase(before);
                                _confirmed = before;
                                _rebaseAfterYield = false;
                            }
                            CheckReplacement(before, Stopwatch.GetTimestamp());
                            if (Reason != SmoothingStop.None) break;
                            Pixel target = _motion.Sample(now);
                            if (!_pending && target != before && Healthy(Stopwatch.GetTimestamp()))
                            {
                                packet.Mouse.X = Native.Normalize(target.X, _bounds.X, _bounds.Width);
                                packet.Mouse.Y = Native.Normalize(target.Y, _bounds.Y, _bounds.Height);
                                packet.Mouse.Extra = _pendingTag = Native.NextInputTag();
                                _target = target; _submittedFrom = before; _issued = now; _pending = true; _observed = false;
                                if (Native.SendInput(1, ref packet, Marshal.SizeOf<Native.Input>()) != 1) Stop(SmoothingStop.OutputError);
                            }
                        }
                        if (Reason != SmoothingStop.None) break;
                        uint result = Native.MsgWaitForMultipleObjectsEx(2, handles, 10, Native.AllInput, Native.InputAvailable);
                        if (result != 0 && result != 1 && result != 2 && result != Native.WaitTimeout) Stop(SmoothingStop.NativeError);
                    }
                }
                catch (Exception ex) { Error = ex.Message; Stop(SmoothingStop.NativeError); }
                finally
                {
                    Stop(SmoothingStop.Disabled);
                    Volatile.Write(ref _running, 0);
                    Ready.TrySetResult(false);
                    bool unhooked = _hook == 0 || Native.UnhookWindowsHookEx(_hook);
                    if (!unhooked)
                    {
                        Error = "UnhookWindowsHookEx failed: " + Marshal.GetLastWin32Error();
                        Retained = true; _retained = this;
                    }
                    if (_clockStarted) _clock!.Join();
                    if (_timer != 0 && !Native.CloseHandle(_timer)) { Retained = true; _retained = this; }
                    bool windowDestroyed = true;
                    if (_rawInput) Native.RegisterRawInputDevices(RawInputDevices(Native.RawInputRemove, 0), 2, (uint)Marshal.SizeOf<Native.RawInputDevice>());
                    if (_power != 0) Native.UnregisterPowerSettingNotification(_power);
                    if (_sessionNotifications) Native.WTSUnRegisterSessionNotification(_window);
                    if (_window != 0) windowDestroyed = Native.DestroyWindow(_window);
                    if (!windowDestroyed) { Retained = true; _retained = this; }
                    if (_classRegistered && windowDestroyed && !Native.UnregisterClassW(_className!, Native.GetModuleHandleW(null)))
                    { Retained = true; _retained = this; }
                    _motion = null;
                    if (_library != 0) NativeLibrary.Free(_library);
                    if (!Retained)
                    {
                        bool workerClosed = _worker == 0 || Native.CloseHandle(_worker);
                        bool controllerClosed = _controller == 0 || Native.CloseHandle(_controller);
                        if (!workerClosed || !controllerClosed) { Retained = true; _retained = this; }
                    }
                    if (Retained)
                    {
                        Error = $"Cleanup failed after {Reason}. {Error}";
                        Interlocked.Exchange(ref _reason, (int)SmoothingStop.CleanupError);
                    }
                    lock (_signalGate)
                    {
                        _finished = true;
                        _stop.Dispose(); _wake.Dispose();
                    }
                    Done.TrySetResult(true);
                    GC.KeepAlive(_hookProc); GC.KeepAlive(_windowProc);
                }
            }
        }
    }
}
