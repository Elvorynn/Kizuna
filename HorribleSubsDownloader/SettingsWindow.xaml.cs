using MahApps.Metro.Controls;
using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Kizuna
{
    public partial class SettingsWindow : MetroWindow
    {
        public bool ResetLayoutRequested { get; private set; }

        // Retry delay options: label → ms
        private static readonly (string Label, int Ms)[] _retryDelays =
        {
            ("5 s",   5000),
            ("10 s",  10000),
            ("30 s",  30000),
            ("1 min", 60000),
            ("2 min", 120000),
            ("5 min", 300000),
        };

        public SettingsWindow()
        {
            InitializeComponent();
            PopulateRetryDelayComboBox();
            PopulateFieldsFromStorage();
            ApplyLanguage();

            CboLanguage.SelectionChanged += CboLanguage_SelectionChanged;
        }

        // ── Initialization ────────────────────────────────────────────────────

        private void PopulateRetryDelayComboBox()
        {
            CboRetryDelay.Items.Clear();
            foreach (var (label, _) in _retryDelays)
                CboRetryDelay.Items.Add(label);
            CboRetryDelay.SelectedIndex = 1; // default: 10 s
        }

        private void PopulateFieldsFromStorage()
        {
            AppSettings s = SettingsStorage.Load();
            if (s == null) { CboLanguage.SelectedIndex = 0; return; }

            TxtApiBaseUrl.Text          = s.ApiBaseUrl;
            TxtUsername.Text            = s.Username;
            TxtApiToken.Text            = s.ApiToken;
            TxtTorrentDownloadPath.Text = s.TorrentDownloadPath;
            TxtTransmissionUrl.Text     = s.TransmissionUrl;
            CboLanguage.SelectedIndex   = LanguageToIndex(s.Language);

            ChkMinimizeToTray.IsChecked    = s.MinimizeToTray;
            ChkStartWithWindows.IsChecked  = s.StartWithWindows;
            ChkNotifications.IsChecked     = s.NotificationsEnabled;
            ChkAutoRetry.IsChecked         = s.AutoRetryEnabled;
            ChkReResolveLibrary.IsChecked  = s.ReResolveLibraryOnScan;

            // Select retry delay
            int delayIdx = 1;
            for (int i = 0; i < _retryDelays.Length; i++)
            {
                if (_retryDelays[i].Ms == s.AutoRetryDelayMs)
                { delayIdx = i; break; }
            }
            CboRetryDelay.SelectedIndex = delayIdx;

            // Select conflict behavior
            SelectConflictItem(s.OnFileConflict ?? "ask");
        }

        private void SelectConflictItem(string tag)
        {
            foreach (ComboBoxItem item in CboFileConflict.Items)
            {
                if (item.Tag?.ToString() == tag)
                { CboFileConflict.SelectedItem = item; return; }
            }
            CboFileConflict.SelectedIndex = 0;
        }

        private void ApplyLanguage()
        {
            Title                = LocalizationManager.Get("SettingsTitle");
            TxtSectionApi.Text   = LocalizationManager.Get("SettingsApiSection");
            TxtSectionTx.Text    = LocalizationManager.Get("SettingsTxSection");
            TxtSectionLang.Text  = LocalizationManager.Get("SettingsLangSection");
            TxtSectionPaths.Text = LocalizationManager.Get("SettingsPathsSection");
            BtnSave.Content      = LocalizationManager.Get("BtnSave");
            BtnCancel.Content    = LocalizationManager.Get("BtnCancel");
            BtnBrowse.Content    = LocalizationManager.Get("BtnBrowse");
            BtnResetGui.Content  = LocalizationManager.Get("BtnResetGui");

            ErrApiBaseUrl.Text          = LocalizationManager.Get("ErrApiBaseUrl");
            ErrUsername.Text            = LocalizationManager.Get("ErrUsername");
            ErrApiToken.Text            = LocalizationManager.Get("ErrApiToken");
            ErrTorrentDownloadPath.Text = LocalizationManager.Get("ErrTorrentPath");

            TextBoxHelper.SetWatermark(TxtApiBaseUrl,          LocalizationManager.Get("WatermarkUrl"));
            TextBoxHelper.SetWatermark(TxtUsername,            LocalizationManager.Get("WatermarkUsername"));
            TextBoxHelper.SetWatermark(TxtApiToken,            LocalizationManager.Get("WatermarkToken"));
            TextBoxHelper.SetWatermark(TxtTorrentDownloadPath, LocalizationManager.Get("WatermarkPath"));
        }

        // ── Validation ────────────────────────────────────────────────────────

        private bool ValidateAll()
            => ValidateApiBaseUrl() & ValidateUsername() & ValidateApiToken() & ValidateTorrentPath();

        private bool ValidateApiBaseUrl()
        {
            string raw = TxtApiBaseUrl.Text.Trim();
            bool ok = Uri.TryCreate(raw, UriKind.Absolute, out Uri parsed)
                   && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
            ErrApiBaseUrl.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            return ok;
        }

        private bool ValidateUsername()
        {
            bool ok = !string.IsNullOrWhiteSpace(TxtUsername.Text);
            ErrUsername.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            return ok;
        }

        private bool ValidateApiToken()
        {
            bool ok = !string.IsNullOrWhiteSpace(TxtApiToken.Text);
            ErrApiToken.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            return ok;
        }

        private bool ValidateTorrentPath()
        {
            string raw = TxtTorrentDownloadPath.Text.Trim();
            bool ok = !string.IsNullOrWhiteSpace(raw)
                   && Path.IsPathRooted(raw)
                   && raw.IndexOfAny(Path.GetInvalidPathChars()) < 0;
            ErrTorrentDownloadPath.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            return ok;
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void CboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LocalizationManager.SetLanguage(IndexToLanguage(CboLanguage.SelectedIndex));
            ApplyLanguage();
        }

        private void TxtField_TextChanged(object sender, TextChangedEventArgs e)
        {
            var box = (TextBox)sender;
            if      (box == TxtApiBaseUrl)          ErrApiBaseUrl.Visibility          = Visibility.Collapsed;
            else if (box == TxtUsername)             ErrUsername.Visibility            = Visibility.Collapsed;
            else if (box == TxtApiToken)             ErrApiToken.Visibility            = Visibility.Collapsed;
            else if (box == TxtTorrentDownloadPath)  ErrTorrentDownloadPath.Visibility = Visibility.Collapsed;
        }

        private void TxtApiBaseUrl_LostFocus(object sender, RoutedEventArgs e)
            => ValidateApiBaseUrl();
        private void TxtUsername_LostFocus(object sender, RoutedEventArgs e)
            => ValidateUsername();
        private void TxtApiToken_LostFocus(object sender, RoutedEventArgs e)
            => ValidateApiToken();
        private void TxtTorrentDownloadPath_LostFocus(object sender, RoutedEventArgs e)
            => ValidateTorrentPath();

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            string picked = FolderPicker.Pick(
                initialPath: TxtTorrentDownloadPath.Text,
                title: "Select torrent download folder");
            if (picked != null)
            {
                TxtTorrentDownloadPath.Text       = picked;
                ErrTorrentDownloadPath.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateAll()) return;

            bool startWithWindows = ChkStartWithWindows.IsChecked == true;
            string txUrl = TxtTransmissionUrl.Text.Trim();
            string conflictTag = (CboFileConflict.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ask";
            int retryMs = CboRetryDelay.SelectedIndex >= 0 && CboRetryDelay.SelectedIndex < _retryDelays.Length
                ? _retryDelays[CboRetryDelay.SelectedIndex].Ms
                : 10000;

            // Update() loads-modifies-saves so unrelated settings (watched/duplicate
            // folders, rules, layout) are always preserved.
            SettingsStorage.Update(s =>
            {
                s.ApiBaseUrl          = TxtApiBaseUrl.Text.Trim();
                s.Username            = TxtUsername.Text.Trim();
                s.ApiToken            = TxtApiToken.Text.Trim();
                s.TorrentDownloadPath = TxtTorrentDownloadPath.Text.Trim();
                s.Language            = IndexToLanguage(CboLanguage.SelectedIndex);
                s.TransmissionUrl     = string.IsNullOrEmpty(txUrl)
                    ? "http://127.0.0.1:9091/transmission/rpc"
                    : txUrl;
                s.MinimizeToTray         = ChkMinimizeToTray.IsChecked == true;
                s.StartWithWindows       = startWithWindows;
                s.NotificationsEnabled   = ChkNotifications.IsChecked == true;
                s.AutoRetryEnabled       = ChkAutoRetry.IsChecked == true;
                s.ReResolveLibraryOnScan = ChkReResolveLibrary.IsChecked == true;
                s.AutoRetryDelayMs       = retryMs;
                s.OnFileConflict         = conflictTag;
            });

            // Apply autostart registry setting
            ApplyAutostart(startWithWindows);

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;

        private void BtnResetGui_Click(object sender, RoutedEventArgs e)
        {
            ResetLayoutRequested = true;
            DialogResult = true;
        }

        // ── Autostart (registry) ──────────────────────────────────────────────

        private static readonly string AutostartKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutostartValueName = "Kizuna";

        public static void ApplyAutostart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AutostartKeyPath, writable: true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        key.SetValue(AutostartValueName, $"\"{exePath}\" --silent");
                    }
                    else
                    {
                        key.DeleteValue(AutostartValueName, throwOnMissingValue: false);
                    }
                }
            }
            catch { /* Registry not accessible — silently ignore */ }
        }

        // ── Language helpers ──────────────────────────────────────────────────

        private static int LanguageToIndex(string lang)
        {
            switch (lang)
            {
                case "German":   return 1;
                case "French":   return 2;
                case "Spanish":  return 3;
                case "Japanese": return 4;
                default:         return 0;
            }
        }

        private static string IndexToLanguage(int index)
        {
            switch (index)
            {
                case 1: return "German";
                case 2: return "French";
                case 3: return "Spanish";
                case 4: return "Japanese";
                default: return "English";
            }
        }
    }
}
