using Microsoft.UI.Xaml;
using AmtPtpControlPanel.WinUI3.Smoothing;

namespace AmtPtpControlPanel.WinUI3
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
            LocalizationManager.Initialize();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // A recovery restart resumes smoothing and starts in the tray.
            string[] arguments = System.Environment.GetCommandLineArgs();
            SmoothingRecovery? recovery = SmoothingRecovery.Parse(arguments);
            bool background = recovery.HasValue || System.Array.IndexOf(arguments, "--background") >= 0;
            var window = new MainWindow(recovery, background);

            // Set the size before activation so the window does not visibly
            // resize shortly after it appears.
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1121, 774));

            _window = window;
            if (background)
            {
                window.AppWindow.Show(false);
                window.AppWindow.Hide();
            }
            else
            {
                window.Activate();
            }
        }
    }
}
