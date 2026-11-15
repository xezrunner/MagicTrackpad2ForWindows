using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AmtPtpControlPanel.WinUI3.Smoothing;

namespace AmtPtpControlPanel.WinUI3.Input
{
    /// <summary>
    /// Owns the app's trackpad raw-input registration, which Windows allows once per process. Records when the
    /// trackpad last reported, and turns a held two-finger click into a held right button. Runs until the process exits.
    /// </summary>
    internal static class TrackpadInput
    {
        private static readonly Native.HookProc HookProc = Hook;
        private static readonly Native.WindowProc WindowProc = WindowMessage;
        private const uint SendButtonMessage = 0x8000;
        private static Thread? _thread;
        private static nint _window;
        private static long _reportAt;

        // Owned by the input thread, which runs both the hook and WM_INPUT.
        private static readonly Dictionary<nint, byte[]?> Trackpads = new Dictionary<nint, byte[]?>();
        private static readonly Native.UsageAndPage[] Usages = new Native.UsageAndPage[32];
        private static byte[] _input = new byte[512];
        private static bool _buttonDown;
        private static int _pressContacts;
        private static bool _rightHeld;

        internal static long LastReportAt => Volatile.Read(ref _reportAt);

        internal static void Start()
        {
            if (_thread != null)
                return;

            _thread = new Thread(Run) { IsBackground = true, Name = "Trackpad input" };
            _thread.Start();
        }

        private static void Check(bool success)
        {
            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static void Run()
        {
            try
            {
                nint instance = Native.GetModuleHandleW(null);
                Native.WindowClass windowClass = new Native.WindowClass
                {
                    Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                    Proc = WindowProc,
                    Instance = instance,
                    Name = "MT2FWTrackpadInput"
                };
                Check(Native.RegisterClassExW(ref windowClass) != 0);

                _window = Native.CreateWindowExW(0, windowClass.Name, "", 0, 0, 0, 0, 0, Native.MessageOnlyParent, 0, instance, 0);
                Check(_window != 0);

                Native.RawInputDevice[] devices =
                {
                    new Native.RawInputDevice { UsagePage = Native.DigitizerPage, Usage = Native.TouchpadUsage, Flags = Native.RawInputSink, Target = _window }
                };
                Check(Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Native.RawInputDevice>()));
                Check(Native.SetWindowsHookExW(Native.MouseLowLevelHook, HookProc, instance, 0) != 0);

                while (Native.GetMessageW(out Native.Message message, 0, 0, 0) > 0)
                    Native.DispatchMessageW(ref message);
            }
            catch (Win32Exception ex)
            {
                Debug.WriteLine("Trackpad input: " + ex.Message);
            }
        }

        private static nint WindowMessage(nint window, uint message, nuint wParam, nint lParam)
        {
            if (message == Native.RawInput)
                ReadInput(lParam);

            if (message == SendButtonMessage)
                SendButton((uint)wParam);

            return Native.DefWindowProcW(window, message, wParam, lParam);
        }

        private static void ReadInput(nint input)
        {
            uint size = 0;
            if (Native.GetRawInputBytes(input, Native.RawInputDataCommand, null, ref size, Native.RawHeaderSize) != 0)
                return;

            if (_input.Length < size)
                _input = new byte[size];

            if (Native.GetRawInputBytes(input, Native.RawInputDataCommand, _input, ref size, Native.RawHeaderSize) != size)
                return;

            Native.RawHeader header = MemoryMarshal.Read<Native.RawHeader>(_input);
            byte[]? preparsed = header.Type == Native.RawHidType ? TrackpadLayout(header.Device) : null;
            if (preparsed == null)
                return;

            Volatile.Write(ref _reportAt, Stopwatch.GetTimestamp());

            // RAWHID: report size, report count, then the reports.
            int offset = (int)Native.RawHeaderSize;
            int reportSize = BitConverter.ToInt32(_input, offset);
            int count = BitConverter.ToInt32(_input, offset + 4);
            for (int i = 0; i < count; i++)
                ReadReport(preparsed, offset + 8 + i * reportSize, reportSize);
        }

        // Returns the trackpad's preparsed HID data, or null for other devices.
        private static byte[]? TrackpadLayout(nint device)
        {
            if (Trackpads.TryGetValue(device, out byte[]? preparsed))
                return preparsed;

            Native.RawDeviceInfo info = new Native.RawDeviceInfo { Size = Native.RawDeviceInfoSize };
            uint size = Native.RawDeviceInfoSize;
            if (Native.GetRawInputDeviceInfoW(device, Native.RawDeviceInfoCommand, ref info, ref size) != Native.RawDeviceInfoSize)
                return null;

            if (info.Type == Native.RawHidType && info.VendorId == Native.TrackpadVendor)
            {
                size = 0;
                Native.GetRawInputDeviceBytes(device, Native.PreparsedDataCommand, null, ref size);
                preparsed = new byte[size];
                if (Native.GetRawInputDeviceBytes(device, Native.PreparsedDataCommand, preparsed, ref size) != size)
                    return null;
            }

            Trackpads[device] = preparsed;
            return preparsed;
        }

        // The report's ON buttons include the click button and one tip switch per touching finger.
        private static void ReadReport(byte[] preparsed, int offset, int length)
        {
            uint count = (uint)Usages.Length;
            if (Native.HidP_GetUsagesEx(Native.HidInputReport, 0, Usages, ref count, preparsed, ref _input[offset], (uint)length) != Native.HidSuccess)
                return;

            bool button = false;
            int contacts = 0;
            for (int i = 0; i < count; i++)
            {
                if (Usages[i].UsagePage == Native.ButtonPage && Usages[i].Usage == 1)
                    button = true;
                else if (Usages[i].UsagePage == Native.DigitizerPage && Usages[i].Usage == Native.TipSwitchUsage)
                    contacts++;
            }

            if (button && !_buttonDown)
                _pressContacts = contacts;

            _buttonDown = button;
        }

        // Windows turns a held two-finger click into a left drag, sending the left press 150 to 300 ms after the click.
        // By then the click's report has arrived. A quick two-finger click stays the right click Windows already sends.
        private static nint Hook(int code, nuint message, nint data)
        {
            if (code == 0 && (Marshal.PtrToStructure<Native.MouseHook>(data).Flags & Native.Injected) == 0)
            {
                if (message == Native.LeftDown && _buttonDown && _pressContacts == 2)
                {
                    _rightHeld = true;
                    return SendLater(Native.RightDownInput);
                }

                if (message == Native.LeftUp && _rightHeld)
                {
                    _rightHeld = false;
                    return SendLater(Native.RightUpInput);
                }
            }

            return Native.CallNextHookEx(0, code, message, data);
        }

        // Input sent inside the hook waits for this same hook to time out, delaying it by 300 ms.
        private static nint SendLater(uint flags)
        {
            Native.PostMessageW(_window, SendButtonMessage, flags, 0);
            return 1;
        }

        private static void SendButton(uint flags)
        {
            Native.Input input = new Native.Input { Mouse = new Native.MouseInput { Flags = flags } };
            Native.SendInput(1, ref input, Marshal.SizeOf<Native.Input>());
        }
    }
}
