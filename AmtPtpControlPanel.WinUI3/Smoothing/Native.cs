using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    /// <summary>
    /// Win32 declarations shared by pointer smoothing and the tray.
    /// </summary>
    internal static class Native
    {
        internal const uint HighResolutionTimer = 2;
        internal const uint TimerAccess = 0x00100002;

        internal const nuint InputTag = 0x4d543253;
        internal const nuint InputTagMask = 0xffff0000;
        private static int _inputSequence = Environment.TickCount & 0xffff;

        internal const int ErrorAlreadyExists = 183;
        internal const int ErrorInvalidHookHandle = 1404;
        internal const int MouseLowLevelHook = 14;

        internal const uint WaitTimeout = 258;
        internal const uint AllInput = 0x1cff;
        internal const uint InputAvailable = 4;
        internal const uint RemoveMessage = 1;

        internal const uint DisplayChange = 0x7e;
        internal const uint SessionChange = 0x2b1;
        internal const uint PowerBroadcast = 0x218;

        internal const uint PowerSuspend = 4;
        internal const uint PowerResumeSuspend = 7;
        internal const uint PowerResumeAutomatic = 18;
        internal const uint PowerSettingChange = 0x8013;
        internal static readonly Guid DisplayStatus = new Guid("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");

        internal const uint MouseMove = 0x200;
        internal const uint LeftDown = 0x201;
        internal const uint LeftUp = 0x202;
        internal const uint RightDown = 0x204;
        internal const uint RightUp = 0x205;
        internal const uint MiddleDown = 0x207;
        internal const uint MiddleUp = 0x208;
        internal const uint XDown = 0x20b;
        internal const uint XUp = 0x20c;

        internal const uint Injected = 1;
        internal const uint AbsoluteMove = 0x8000 | 0x4000 | 1;
        internal const uint MonitorDefaultToNull = 0;

        internal const uint RawInput = 0xff;
        internal const uint RawInputHeaderCommand = 0x10000005;
        internal const uint RawInputDataCommand = 0x10000003;
        internal const uint RawDeviceInfoCommand = 0x2000000b;
        internal const uint RawMouseType = 0;
        internal const uint RawHidType = 2;
        internal const uint RawInputSink = 0x100;
        internal const uint RawInputRemove = 1;
        internal static readonly uint RawHeaderSize = (uint)Marshal.SizeOf<RawHeader>();
        internal static readonly uint RawMouseSize = (uint)Marshal.SizeOf<RawMouse>();
        internal static readonly uint RawDeviceInfoSize = (uint)Marshal.SizeOf<RawDeviceInfo>();

        // Both MT2FW drivers report this vendor ID for the trackpad's PTP collection.
        internal const uint TrackpadVendor = 0x8910;
        internal const ushort DigitizerPage = 0x0d;
        internal const ushort TouchpadUsage = 0x05;
        internal const ushort DesktopPage = 0x01;
        internal const ushort MouseUsage = 0x02;

        internal static nuint NextInputTag()
        {
            return (InputTag & InputTagMask) | (nuint)(Interlocked.Increment(ref _inputSequence) & 0xffff);
        }

        internal static bool IsSmoothingInput(nuint extra)
        {
            return (extra & InputTagMask) == (InputTag & InputTagMask);
        }

        internal static int Normalize(int pixel, int origin, int size)
        {
            return (int)((((long)pixel - origin) * 65536 + 32768) / size);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint HookProc(int code, nuint message, nint data);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate uint CompositorWait(uint count, [In] nint[] handles, uint timeout);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseHook
        {
            internal Point Point;
            internal uint Data;
            internal uint Flags;
            internal uint Time;
            internal nuint Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint Data;
            internal uint Flags;
            internal uint Time;
            internal nuint Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Kind;
            internal MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal nint Window;
            internal uint Id;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal Point Point;
            internal uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawInputDevice
        {
            internal ushort UsagePage;
            internal ushort Usage;
            internal uint Flags;
            internal nint Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawHeader
        {
            internal uint Type;
            internal uint Size;
            internal nint Device;
            internal nuint WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawMouse
        {
            internal RawHeader Header;
            internal ushort Flags;
            internal ushort Padding;
            internal ushort ButtonFlags;
            internal ushort ButtonData;
            internal uint RawButtons;
            internal int X;
            internal int Y;
            internal uint Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawDeviceInfo
        {
            internal uint Size;
            internal uint Type;
            internal uint VendorId;
            internal uint ProductId;
            internal uint Version;
            internal ushort UsagePage;
            internal ushort Usage;
            internal uint Padding1;
            internal uint Padding2;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Size;
            internal uint Style;
            internal WindowProc Proc;
            internal int ClassExtra;
            internal int WindowExtra;
            internal nint Instance;
            internal nint Icon;
            internal nint Cursor;
            internal nint Background;
            internal string? Menu;
            internal string Name;
            internal nint SmallIcon;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimerEx(nint timer, in long dueTime, int period, nint callback, nint argument, nint context, uint tolerance);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForMultipleObjects(uint count, [In] nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint timeout);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);

        [DllImport("user32.dll")]
        internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPhysicalCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClipCursor(out Rect rect);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromPoint(Point point, uint flags);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, ref Input input, int size);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

        [DllImport("user32.dll", EntryPoint = "GetRawInputData")]
        internal static extern uint GetRawInputHeader(nint input, uint command, out RawHeader header, ref uint size, uint headerSize);

        [DllImport("user32.dll", EntryPoint = "GetRawInputData")]
        internal static extern uint GetRawMouse(nint input, uint command, out RawMouse mouse, ref uint size, uint headerSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint GetRawInputDeviceInfoW(nint device, uint command, ref RawDeviceInfo info, ref uint size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint MsgWaitForMultipleObjectsEx(uint count, [In] nint[] handles, uint timeout, uint mask, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);

        [DllImport("user32.dll")]
        internal static extern nint DispatchMessageW(ref Message message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowExW(
            uint exStyle,
            string className,
            string name,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint param);

        [DllImport("user32.dll")]
        internal static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClassW(string name, nint instance);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint RegisterPowerSettingNotification(nint window, in Guid setting, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterPowerSettingNotification(nint handle);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSRegisterSessionNotification(nint window, uint flags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSUnRegisterSessionNotification(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformationW(nint handle, int index, [Out] char[] info, uint length, out uint needed);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(nint desktop);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateMutexW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint handle);
    }
}
