using System.Runtime.InteropServices;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    /// <summary>
    /// Keeps Windows from slowing the smoothing clock while the app is hidden in the tray.
    /// </summary>
    internal static class SmoothingPowerPolicy
    {
        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;
        private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 4;

        // Windows 11 may throttle execution and ignore timer resolution for a hidden process.
        // The opt-out stays on while smoothing is off, when the process is idle anyway.
        internal static void Apply()
        {
            // Older Windows versions reject the timer-resolution bit.
            if (!OptOut(PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION))
                OptOut(PROCESS_POWER_THROTTLING_EXECUTION_SPEED);
        }

        private static bool OptOut(uint flags)
        {
            PowerThrottling state = new PowerThrottling
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = flags,
                StateMask = 0,
            };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, in state, (uint)Marshal.SizeOf<PowerThrottling>());
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

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(nint process, int informationClass, in PowerThrottling state, uint size);
    }
}
