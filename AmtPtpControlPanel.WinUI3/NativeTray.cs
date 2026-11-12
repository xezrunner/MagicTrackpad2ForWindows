using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AmtPtpControlPanel.WinUI3.Smoothing;

namespace AmtPtpControlPanel.WinUI3
{
    /// <summary>
    /// Notification-area icon and menu that keep the control panel reachable while its window is hidden.
    /// Because it subclasses the main window, it also tells smoothing when the session, power, or display
    /// state allows a run.
    /// </summary>
    internal sealed class NativeTray : IDisposable
    {
        private const uint CallbackMessage = 0x8001;
        private const uint IconId = 1;
        private const uint Version4 = 4;

        private const uint WM_CONTEXTMENU = 0x7b;
        private const uint WM_NCDESTROY = 0x82;
        private const uint WM_GETICON = 0x7f;
        private const uint ICON_SMALL2 = 2;

        private const uint NIN_SELECT = 0x400;
        private const uint NIN_KEYSELECT = 0x401;
        private const uint NIM_ADD = 0;
        private const uint NIM_MODIFY = 1;
        private const uint NIM_DELETE = 2;
        private const uint NIM_SETVERSION = 4;
        private const uint NIF_MESSAGE = 1;
        private const uint NIF_ICON = 2;
        private const uint NIF_TIP = 4;
        private const uint NIF_SHOWTIP = 0x80;

        private const int IDI_APPLICATION = 32512;
        private const int WTSSessionInfoEx = 25;

        private const uint MF_CHECKED = 8;
        private const uint MF_SEPARATOR = 0x800;
        private const uint TPM_RETURNCMD = 0x100;
        private const uint TPM_RIGHTBUTTON = 2;

        private readonly nint _window;
        private readonly SubclassProc _callback;
        private readonly Action _open;
        private readonly Action _toggle;
        private readonly Action _copyDiagnostics;
        private readonly Func<Task> _quit;
        private readonly Func<bool> _enabled;
        private readonly Action<bool, bool> _environmentChanged;
        private readonly uint _taskbarCreated;
        private NotifyIcon _icon;

        private bool _disposed;
        private bool _sessionRegistered;
        private bool _displayOn = true;
        private bool _suspended;
        private nint _power;
        private long _nextAdd;
        private long _nextIconCheck;
        private long _nextEnvironment;
        private long _topologyUntil;
        private string _tooltip = "Magic Trackpad 2";
        private string _status = "Stopped";

        internal bool IsVisible { get; private set; }

        internal NativeTray(nint window, Action open, Action toggle, Func<Task> quit, Func<bool> enabled,
            Action<bool, bool> environmentChanged, Action copyDiagnostics)
        {
            _window = window;
            _open = open;
            _toggle = toggle;
            _quit = quit;
            _enabled = enabled;
            _environmentChanged = environmentChanged;
            _copyDiagnostics = copyDiagnostics;
            _callback = WindowMessage;
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            if (_taskbarCreated == 0 || !SetWindowSubclass(window, _callback, IconId, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            ChangeWindowMessageFilterEx(window, _taskbarCreated, 1, 0);
            ChangeWindowMessageFilterEx(window, CallbackMessage, 1, 0);

            // WM_GETICON and LoadIcon return icons owned by the window or system, not this tray object.
            nint image = SendMessageW(window, WM_GETICON, ICON_SMALL2, 0);
            if (image == 0)
                image = LoadIconW(0, (nint)IDI_APPLICATION);

            _icon = new NotifyIcon
            {
                Size = (uint)Marshal.SizeOf<NotifyIcon>(),
                Window = window,
                Id = IconId,
                Callback = CallbackMessage,
                Image = image,
                Tip = _tooltip,
                Info = "",
                InfoTitle = ""
            };
        }

        internal void Update(string status)
        {
            if (_disposed)
                return;

            long now = Stopwatch.GetTimestamp();
            _status = status;
            string tip = "Magic Trackpad 2: " + status;
            if (tip.Length > 127)
                tip = tip[..127];

            bool changed = tip != _tooltip;
            _icon.Tip = tip;
            _tooltip = tip;
            if (!IsVisible && now >= _nextAdd)
            {
                _nextAdd = now + Stopwatch.Frequency * 2;
                _icon.Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
                if (Shell_NotifyIconW(NIM_ADD, ref _icon) || Shell_NotifyIconW(NIM_MODIFY, ref _icon))
                {
                    _icon.Version = Version4;
                    IsVisible = Shell_NotifyIconW(NIM_SETVERSION, ref _icon);
                    if (!IsVisible)
                        RemoveIcon();
                }
            }
            else if (IsVisible && (changed || now >= _nextIconCheck))
            {
                _nextIconCheck = now + Stopwatch.Frequency * 5;
                _icon.Flags = NIF_TIP | NIF_SHOWTIP;
                if (!Shell_NotifyIconW(NIM_MODIFY, ref _icon))
                    RemoveIcon();
            }

            if (now < _nextEnvironment)
                return;

            _nextEnvironment = now + Stopwatch.Frequency * 19 / 20;
            if (!_sessionRegistered)
                _sessionRegistered = Native.WTSRegisterSessionNotification(_window, 0);

            if (_power == 0)
                _power = Native.RegisterPowerSettingNotification(_window, Native.DisplayStatus, 0);

            bool ready = _sessionRegistered && _power != 0 && _displayOn && !_suspended &&
                now >= _topologyUntil && SessionIsInteractive();
            _environmentChanged(ready, false);
        }

        // Update adds the icon again on its next call.
        internal void RemoveIcon()
        {
            NotifyIcon removal = _icon;
            removal.Flags = 0;
            Shell_NotifyIconW(NIM_DELETE, ref removal);
            IsVisible = false;
        }

        private static bool SessionIsInteractive()
        {
            if (!WTSQuerySessionInformationW(0, 0xffffffff, WTSSessionInfoEx, out nint data, out uint bytes))
                return false;

            try
            {
                // SDK-verified WTSINFOEXW level-one offsets; query only connection and lock state.
                return bytes >= 20 && Marshal.ReadInt32(data) == 1 &&
                    Marshal.ReadInt32(data, 12) == 0 && Marshal.ReadInt32(data, 16) == 1;
            }
            finally
            {
                WTSFreeMemory(data);
            }
        }

        private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
        {
            try
            {
                if (_disposed)
                    return DefSubclassProc(window, message, wParam, lParam);

                if (message == _taskbarCreated)
                {
                    IsVisible = false;
                    _nextAdd = 0;
                    Update(_status);
                }

                if (message == CallbackMessage && ((ulong)lParam >> 16 & 0xffff) == IconId)
                {
                    uint notification = (uint)((ulong)lParam & 0xffff);
                    if (notification == NIN_SELECT || notification == NIN_KEYSELECT)
                        _open();
                    else if (notification == WM_CONTEXTMENU)
                        ShowMenu(wParam);

                    return 0;
                }

                if (message == Native.SessionChange && (wParam == 1 || wParam == 2 || wParam == 3 ||
                    wParam == 4 || wParam == 5 || wParam == 6 || wParam == 7 || wParam == 8 || wParam == 15))
                {
                    _environmentChanged(false, true);
                    _nextEnvironment = 0;
                }

                if (message == Native.DisplayChange)
                {
                    _topologyUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
                    _environmentChanged(false, true);
                    _nextEnvironment = _topologyUntil;
                }

                if (message == Native.PowerBroadcast)
                {
                    bool wasReady = _displayOn && !_suspended;
                    if (wParam == Native.PowerSuspend)
                        _suspended = true;

                    if (wParam == Native.PowerResumeSuspend || wParam == Native.PowerResumeAutomatic)
                        _suspended = false;

                    if (wParam == Native.PowerSettingChange && lParam != 0 &&
                        Marshal.PtrToStructure<Guid>(lParam) == Native.DisplayStatus && Marshal.ReadInt32(lParam, 16) == 4)
                        _displayOn = Marshal.ReadInt32(lParam, 20) != 0;

                    if (wasReady != (_displayOn && !_suspended))
                    {
                        _environmentChanged(false, true);
                        _nextEnvironment = 0;
                    }
                }

                if (message == WM_NCDESTROY)
                    Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Tray callback: " + ex.Message);
            }
            return DefSubclassProc(window, message, wParam, lParam);
        }

        private void ShowMenu(nuint position)
        {
            nint menu = CreatePopupMenu();
            if (menu == 0)
                return;

            try
            {
                if (!AppendMenuW(menu, 0, 1, LocalizationManager.GetString("Smoothing.TrayOpen", "&Open")) ||
                    !AppendMenuW(menu, _enabled() ? MF_CHECKED : 0, 2, LocalizationManager.GetString("Smoothing.TrayEnable", "&Enable smoothing")) ||
                    !AppendMenuW(menu, 0, 4, LocalizationManager.GetString("Smoothing.TrayCopyDiagnostics", "Copy &diagnostics")) ||
                    !AppendMenuW(menu, MF_SEPARATOR, 0, null) ||
                    !AppendMenuW(menu, 0, 3, LocalizationManager.GetString("Smoothing.TrayQuit", "&Quit")))
                    return;

                int x = (short)((ulong)position & 0xffff);
                int y = (short)(((ulong)position >> 16) & 0xffff);
                if (x == -1 && y == -1)
                {
                    IconIdentity identity = new IconIdentity { Size = (uint)Marshal.SizeOf<IconIdentity>(), Window = _window, Id = IconId };
                    if (Shell_NotifyIconGetRect(in identity, out Native.Rect bounds) == 0)
                    {
                        x = bounds.Left;
                        y = bounds.Bottom;
                    }
                    else if (GetCursorPos(out Native.Point point))
                    {
                        x = point.X;
                        y = point.Y;
                    }
                }

                SetForegroundWindow(_window);
                uint command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, _window, 0);
                PostMessageW(_window, 0, 0, 0);
                if (command == 1)
                    _open();
                else if (command == 2)
                    _toggle();
                else if (command == 3)
                    _ = _quit();
                else if (command == 4)
                    _copyDiagnostics();
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            RemoveIcon();
            if (_power != 0)
                Native.UnregisterPowerSettingNotification(_power);

            if (_sessionRegistered)
                Native.WTSUnRegisterSessionNotification(_window);

            RemoveWindowSubclass(_window, _callback, IconId);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIcon
        {
            internal uint Size;
            internal nint Window;
            internal uint Id;
            internal uint Flags;
            internal uint Callback;
            internal nint Image;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string Tip;
            internal uint State;
            internal uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            internal string Info;
            internal uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            internal string InfoTitle;
            internal uint InfoFlags;
            internal Guid Guid;
            internal nint BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconIdentity
        {
            internal uint Size;
            internal nint Window;
            internal uint Id;
            internal Guid Guid;
        }

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);

        [DllImport("comctl32.dll")]
        private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconW(uint message, ref NotifyIcon icon);

        [DllImport("shell32.dll")]
        private static extern int Shell_NotifyIconGetRect(in IconIdentity identity, out Native.Rect bounds);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint change);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint LoadIconW(nint instance, nint name);

        [DllImport("user32.dll")]
        private static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(nint menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Native.Point point);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformationW(nint server, uint session, int info, out nint buffer, out uint bytes);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(nint buffer);
    }
}
