using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    /// <summary>
    /// Opts the process out of execution-speed and timer-resolution throttling while smoothing is enabled.
    /// </summary>
    internal sealed class SmoothingPowerPolicy
    {
        private const int ProcessPowerThrottling = 4;
        private const int ERROR_INVALID_PARAMETER = 87;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;
        private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 4;

        private PowerThrottling _previous;
        private bool _requested;
        private bool _applied;
        private long _nextAttempt;

        internal string? Error;

        internal bool IsApplied => _applied;

        internal void SetEnabled(bool enabled)
        {
            if (enabled != _requested)
            {
                _requested = enabled;
                _nextAttempt = 0;
            }

            if (enabled == _applied || Stopwatch.GetTimestamp() < _nextAttempt)
                return;

            _nextAttempt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            nint process = GetCurrentProcess();
            uint size = (uint)Marshal.SizeOf<PowerThrottling>();
            if (!enabled)
            {
                if (SetProcessInformation(process, ProcessPowerThrottling, in _previous, size))
                {
                    _applied = false;
                    Error = null;
                }
                else
                {
                    Error = "Restoring process scheduling: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }
                return;
            }

            PowerThrottling current = new PowerThrottling { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION };
            if (!GetProcessInformation(process, ProcessPowerThrottling, ref current, size))
            {
                Error = "Reading process scheduling: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return;
            }

            PowerThrottling requested = current;
            uint flags = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
            requested.ControlMask |= flags;
            requested.StateMask &= ~flags;
            bool applied = SetProcessInformation(process, ProcessPowerThrottling, in requested, size);
            if (!applied && Marshal.GetLastWin32Error() == ERROR_INVALID_PARAMETER)
            {
                // Older Windows versions do not support the timer-resolution policy bit.
                requested = current;
                requested.ControlMask |= PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                requested.StateMask &= ~PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                applied = SetProcessInformation(process, ProcessPowerThrottling, in requested, size);
            }

            if (applied)
            {
                _previous = current;
                _applied = true;
                Error = null;
            }
            else
            {
                Error = "Configuring process scheduling: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottling
        {
            internal uint Version;
            internal uint ControlMask;
            internal uint StateMask;
        }

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessInformation(nint process, int informationClass, ref PowerThrottling state, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(nint process, int informationClass, in PowerThrottling state, uint size);
    }
}
