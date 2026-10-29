using System.Windows;

namespace Kizuna
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Always use explicit shutdown — the tray icon keeps the app alive
            // even when no window is visible, so we need to call Shutdown() ourselves.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            bool silent = System.Array.IndexOf(e.Args, "--silent") >= 0;

            AppSettings settings = SettingsStorage.Load();

            if (settings == null)
            {
                var settingsWindow = new SettingsWindow();
                if (settingsWindow.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
                settings = SettingsStorage.Load();
                silent   = false; // first-run: always show the window
            }

            LocalizationManager.SetLanguage(settings?.Language ?? "English");

            var mainWindow = new MainWindow(settings);
            MainWindow = mainWindow;

            if (!silent)
                mainWindow.Show();
            // When silent, the tray icon is already active (created in MainWindow constructor).
        }
    }
}
