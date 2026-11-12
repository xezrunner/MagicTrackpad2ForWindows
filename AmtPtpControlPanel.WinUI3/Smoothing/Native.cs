using System;
using System.Runtime.InteropServices;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    internal static class Native
    {
        internal const uint HighResolutionTimer = 2, TimerAccess = 0x00100002;
        internal const nuint InputTag = 0x4d543253;
        internal const nuint InputTagMask = 0xffff0000;
        private static int _inputSequence = Environment.TickCount & 0xffff;
        internal static nuint NextInputTag() => (InputTag & InputTagMask) | (nuint)(System.Threading.Interlocked.Increment(ref _inputSequence) & 0xffff);
        internal static bool IsSmoothingInput(nuint extra) => (extra & InputTagMask) == (InputTag & InputTagMask);
        internal const int ErrorAlreadyExists = 183, MouseLowLevelHook = 14;
        internal const uint WaitTimeout = 258, AllInput = 0x1cff, InputAvailable = 4, RemoveMessage = 1;
        internal const uint DisplayChange = 0x7e, SessionChange = 0x2b1, PowerBroadcast = 0x218, Quit = 0x12;
        internal const uint PowerSuspend = 4, PowerResumeSuspend = 7, PowerResumeAutomatic = 18, PowerSettingChange = 0x8013;
        internal const uint MouseMove = 0x200, LeftDown = 0x201, LeftUp = 0x202, RightDown = 0x204, RightUp = 0x205;
        internal const uint MiddleDown = 0x207, MiddleUp = 0x208, XDown = 0x20b, XUp = 0x20c;
        internal const uint Injected = 1, AbsoluteMove = 0x8000 | 0x4000 | 1;
        internal const uint MonitorDefaultToNull = 0;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint HookProc(int code, nuint message, nint data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate uint CompositorWait(uint count, [In] nint[] handles, uint timeout);
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X, Y; internal readonly Pixel Pixel => new Pixel(X, Y); }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseHook { internal Point Point; internal uint Data, Flags, Time; internal nuint Extra; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { internal uint Kind; internal MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam;
            internal uint Time; internal Point Point; internal uint Private;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Size, Style; internal WindowProc Proc; internal int ClassExtra, WindowExtra;
            internal nint Instance, Icon, Cursor, Background;
            internal string? Menu; internal string Name; internal nint SmallIcon;
        }

        internal static int Normalize(int pixel, int origin, int size) => (int)((((long)pixel - origin) * 65536 + 32768) / size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimerEx(nint timer, in long dueTime, int period, nint callback, nint argument, nint context, uint tolerance);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForMultipleObjects(uint count, [In] nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint timeout);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);
        [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPhysicalCursorPos(out Point point);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClipCursor(out Rect rect);
        [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, ref Input input, int size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint MsgWaitForMultipleObjectsEx(uint count, [In] nint[] handles, uint timeout, uint mask, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] internal static extern nint DispatchMessageW(ref Message message);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassExW(ref WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterClassW(string name, nint instance);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint RegisterPowerSettingNotification(nint window, in Guid setting, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterPowerSettingNotification(nint handle);
        [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSRegisterSessionNotification(nint window, uint flags);
        [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSUnRegisterSessionNotification(nint window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformationW(nint handle, int index, [Out] char[] info, uint length, out uint needed);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(nint desktop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateMutexW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    }
}
