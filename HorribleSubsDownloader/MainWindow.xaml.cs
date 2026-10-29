using MahApps.Metro.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Kizuna
{
    public partial class MainWindow : MetroWindow
    {
        // ── Services ──────────────────────────────────────────────────────────
        private BackendApiClient    _api;
        private TransmissionService _transmission;

        // ── Settings ──────────────────────────────────────────────────────────
        private string _apiBaseUrl;
        private string _torrentFileDirectory;

        // ── Grid data ────────────────────────────────────────────────────────
        private int _lastKnownRuleCount;
        private ObservableCollection<UnrecognizedFile> _unrecognizedFiles;
        private ObservableCollection<RecognizedFile>   _recognizedFiles;

        // ── Download state ────────────────────────────────────────────────────
        private int  _activeDownloads;     // number of in-flight single-file adds
        private bool _batchActive;         // true while Download All is running
        private int  _batchDelayMs = 5000; // delay between successive adds

        // ── Layout state ─────────────────────────────────────────────────────
        private bool   _logExpanded     = true;
        private double _savedLeftStars  = 1;
        private double _savedRightStars = 2;

        private enum PanelLayout { Normal, LeftFull, RightFull }
        private PanelLayout _panelLayout = PanelLayout.Normal;

        private ScrollViewer _logScrollViewer;

        // ── Library Manager ───────────────────────────────────────────────────
        private ObservableCollection<WatchedFolderItem> _watchedFolders;
        private ObservableCollection<LibraryRule> _libraryRules;
        private ObservableCollection<ScanResult>  _scanResults;
        private LibraryFileCache                  _libraryCache;
        private bool                              _libraryFirstVisit = true;
        private CollectionViewSource              _scanViewSource;
        private string                            _searchText = "";
        private bool                              _moveQueueTab;   // false = Scan Results, true = Move Queue
        private System.Windows.Threading.DispatcherTimer _filterRetryTimer;

        private enum AppView { DownloadManager, LibraryManager, DuplicateFinder }
        private AppView _currentView = AppView.DownloadManager;

        // ── Duplicate Finder ──────────────────────────────────────────────────
        private ObservableCollection<DuplicateFolderItem> _dupFolders;
        private ObservableCollection<DuplicateGroup>       _dupGroups;
        private DuplicateHashCache                         _dupHashCache;
        private CancellationTokenSource                    _dupCts;
        private bool                                       _dupScanning;
        private Point                                      _dupDragStart;
        private DuplicateFolderItem                        _dupDragItem;
        private HashSet<string>                            _dupIgnored;

        // ── System tray ───────────────────────────────────────────────────────
        private TrayIconManager _tray;
        private bool _minimizeToTray = true;
        private bool _exitRequested;

        // ── Grid search ───────────────────────────────────────────────────────
        private string _searchUnrecognized = "";
        private string _searchDownload     = "";

        // ── Download behavior ─────────────────────────────────────────────────
        private bool   _autoRetryEnabled = true;
        private int    _autoRetryDelayMs = 10000;
        private string _conflictBehavior = "ask";
        private bool   _notificationsEnabled;

        // ── Library Manager – undo stack ──────────────────────────────────────
        private readonly Stack<(string From, string To, ScanResult Row)> _moveHistory
            = new Stack<(string, string, ScanResult)>();

        // ── Delay ComboBox ────────────────────────────────────────────────────
        private bool _populatingDelay;

        private static readonly int[] _delayMs =
        {
            0, 1000, 2000, 5000, 10000, 20000, 30000,
            60000, 120000, 300000, 600000, 1800000, 3600000
        };

        // ── Constructor ───────────────────────────────────────────────────────

        public MainWindow(AppSettings settings)
        {
            InitializeComponent();

            ApplySettings(settings);
            _batchDelayMs = settings.BatchDelayMs;
            LoadLibrarySettings(settings);
            RestoreLayout(settings);
            Loaded += (s, e) => UpdateMaxRestoreButton();
            ApplyLanguage();   // populates CboDelay using _batchDelayMs

            InitializeTray();

            RefreshAllGrids();
            RestoreSort(settings);
            _lastKnownRuleCount = FetchRuleCountSafe();
            AppendLog("Application started.");
            StartBackgroundTimers();
        }

        private void InitializeTray()
        {
            _tray = new TrayIconManager();
            _tray.ShowRequested += () =>
            {
                Show();
                WindowState = System.Windows.WindowState.Normal;
                Activate();
            };
            _tray.ExitRequested += () =>
            {
                _exitRequested = true;
                SaveLayout();
                _tray.Dispose();
                Application.Current.Shutdown();
            };
        }

        // ── Setup ─────────────────────────────────────────────────────────────

        private void ApplySettings(AppSettings settings)
        {
            _api = new BackendApiClient(settings.ApiBaseUrl, settings.Username, settings.ApiToken);
            _transmission = new TransmissionService(
                string.IsNullOrWhiteSpace(settings.TransmissionUrl)
                    ? "http://127.0.0.1:9091/transmission/rpc"
                    : settings.TransmissionUrl);
            _apiBaseUrl           = settings.ApiBaseUrl;
            _torrentFileDirectory = settings.TorrentDownloadPath;
            _minimizeToTray       = settings.MinimizeToTray;
            _autoRetryEnabled     = settings.AutoRetryEnabled;
            _autoRetryDelayMs     = settings.AutoRetryDelayMs > 0 ? settings.AutoRetryDelayMs : 10000;
            _conflictBehavior     = settings.OnFileConflict ?? "ask";
            _notificationsEnabled = settings.NotificationsEnabled;
        }

        /// <summary>Shows a tray balloon only when notifications are enabled in settings.</summary>
        private void ShowNotification(string title, string message,
            System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info)
        {
            if (!_notificationsEnabled) return;
            _tray?.ShowBalloon(title, message, icon);
        }

        private void RestoreLayout(AppSettings settings)
        {
            if (!settings.HasSavedLayout) return;

            if (settings.WindowLeft >= 0) Left = settings.WindowLeft;
            if (settings.WindowTop  >= 0) Top  = settings.WindowTop;
            if (settings.WindowWidth  > 200) Width  = settings.WindowWidth;
            if (settings.WindowHeight > 200) Height = settings.WindowHeight;

            if (settings.WindowState == "Maximized")
                WindowState = System.Windows.WindowState.Maximized;

            if (settings.LeftPanelStars > 0 && settings.RightPanelStars > 0)
            {
                GridPanels.ColumnDefinitions[0].Width = new GridLength(settings.LeftPanelStars,  GridUnitType.Star);
                GridPanels.ColumnDefinitions[2].Width = new GridLength(settings.RightPanelStars, GridUnitType.Star);
            }

            _logExpanded = settings.LogExpanded;
            if (!_logExpanded)
            {
                GridMain.RowDefinitions[2].Height = new GridLength(32);
                BtnLogToggle.Content = "▲";
            }
        }

        private void SaveLayout()
            => SettingsStorage.Update(settings =>
        {
            settings.HasSavedLayout = true;
            settings.WindowState    = WindowState == System.Windows.WindowState.Maximized ? "Maximized" : "Normal";

            // When maximized, save the restored (normal) bounds so restoring to normal works correctly
            var bounds = WindowState == System.Windows.WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            settings.WindowLeft   = bounds.Left;
            settings.WindowTop    = bounds.Top;
            settings.WindowWidth  = bounds.Width;
            settings.WindowHeight = bounds.Height;
            settings.LogExpanded  = _logExpanded;

            if (_panelLayout == PanelLayout.Normal)
            {
                double lw    = GridPanels.ColumnDefinitions[0].ActualWidth;
                double rw    = GridPanels.ColumnDefinitions[2].ActualWidth;
                double total = lw + rw;
                if (total > 0)
                {
                    settings.LeftPanelStars  = (lw / total) * 3;
                    settings.RightPanelStars = (rw / total) * 3;
                }
            }
            else
            {
                settings.LeftPanelStars  = _savedLeftStars;
                settings.RightPanelStars = _savedRightStars;
            }

            // Persist current sort (first descriptor only)
            if (GridDownload.Items.SortDescriptions.Count > 0)
            {
                var sd = GridDownload.Items.SortDescriptions[0];
                settings.SortColumnPath = sd.PropertyName;
                settings.SortAscending  = sd.Direction == ListSortDirection.Ascending;
            }
            else
            {
                settings.SortColumnPath = null;
            }
        });

        private void ApplyLanguage()
        {
            Title = LocalizationManager.Get("MainTitle");

            TxtHeaderUnrecognized.Text = LocalizationManager.Get("PanelUnrecognized");
            TxtHeaderDownload.Text     = LocalizationManager.Get("PanelDownload");
            TxtHeaderLog.Text          = LocalizationManager.Get("EventLog");

            BtnSettings.Content    = LocalizationManager.Get("BtnSettings");
            BtnResumeAll.Content   = LocalizationManager.Get("BtnResumeAll");
            BtnPauseAll.Content    = LocalizationManager.Get("BtnPauseAll");
            BtnDownloadAll.Content = LocalizationManager.Get("BtnDownloadAll");

            Resources["KeySetRule"]  = LocalizationManager.Get("BtnSetRule");
            Resources["KeyDownload"] = LocalizationManager.Get("BtnDownload");

            GridUnrecognized.Columns[0].Header = LocalizationManager.Get("ColId");
            GridUnrecognized.Columns[1].Header = LocalizationManager.Get("ColFilename");
            GridDownload.Columns[0].Header     = LocalizationManager.Get("ColId");
            GridDownload.Columns[1].Header     = LocalizationManager.Get("ColFilename");
            GridDownload.Columns[2].Header     = LocalizationManager.Get("ColDate");
            GridDownload.Columns[3].Header     = LocalizationManager.Get("ColFolder");

            TxtEmptyUnrecTitle.Text = LocalizationManager.Get("EmptyUnrecTitle");
            TxtEmptyUnrecSub.Text   = LocalizationManager.Get("EmptyUnrecSub");
            TxtEmptyDlTitle.Text    = LocalizationManager.Get("EmptyDlTitle");
            TxtEmptyDlSub.Text      = LocalizationManager.Get("EmptyDlSub");
            TxtErrUnrecTitle.Text   = LocalizationManager.Get("ErrUnrecTitle");
            TxtErrUnrecSub.Text     = LocalizationManager.Get("ErrUnrecSub");
            TxtErrDlTitle.Text      = LocalizationManager.Get("ErrDlTitle");
            TxtErrDlSub.Text        = LocalizationManager.Get("ErrDlSub");

            TxtCollapsedLeft.Text  = LocalizationManager.Get("PanelUnrecognized");
            TxtCollapsedRight.Text = LocalizationManager.Get("PanelDownload");

            TxtDelayLabel.Text = LocalizationManager.Get("TxtDelayLabel");
            PopulateDelayComboBox();

            // Navigation
            BtnNavDownloadManager.Content = LocalizationManager.Get("NavDownloadManager");
            BtnNavLibraryManager.Content  = LocalizationManager.Get("NavLibraryManager");

            // Library Manager
            TxtHeaderWatchedFolders.Text = LocalizationManager.Get("LibHeaderFolders");
            TxtHeaderRules.Text          = LocalizationManager.Get("LibHeaderRules");
            BtnTabScanResults.Content    = LocalizationManager.Get("LibHeaderScanResults");
            UpdateMoveQueueCount();   // sets the Move Queue tab label (localized + count)
            BtnLibAddFolder.Content      = LocalizationManager.Get("BtnAddFolder");
            BtnLibRemoveFolder.Content   = LocalizationManager.Get("BtnRemove");
            BtnLibAddRule.Content        = LocalizationManager.Get("BtnAddRule");
            BtnLibEditRule.Content       = LocalizationManager.Get("BtnEdit");
            BtnLibRemoveRule.Content     = LocalizationManager.Get("BtnRemove");
            BtnLibScan.Content           = LocalizationManager.Get("BtnScan");

            UpdateItemCount();
        }

        private void StartBackgroundTimers()
        {
            StartTimer(TimeSpan.FromSeconds(5),  Timer_RuleChangeCheck_Tick);
            StartTimer(TimeSpan.FromSeconds(5),  Timer_ProgressPoll_Tick);
            StartTimer(TimeSpan.FromMinutes(10), Timer_PeriodicRefresh_Tick);
            StartTimer(TimeSpan.FromMinutes(5),  Timer_RemoveFinishedTorrents_Tick);
        }

        private void StartTimer(TimeSpan interval, EventHandler handler)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = interval };
            timer.Tick += handler;
            timer.Start();
        }

        // ── Window events ─────────────────────────────────────────────────────

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _logScrollViewer = FindScrollViewer(TxtEventLog);
            TxtEventLog.Document.PageWidth = 5000; // prevent line wrap in RichTextBox

            // Force MahApps title bar to dark even if accent-color overrides don't propagate
            var darkBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            darkBrush.Freeze();
            var titleBar = FindVisualChildByName<Panel>(this, "PART_TitleBar");
            if (titleBar != null) titleBar.Background = darkBrush;

            // Debounce timer: after 3 s of filter inactivity, retry API for local-fallback items
            _filterRetryTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _filterRetryTimer.Tick += FilterRetryTimer_Tick;

            // Recover after the machine wakes from sleep — stale connections are dropped
            // (KeepAlive=false in the API client), so just kick off a fresh refresh.
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;

            // Fires on a system thread — marshal back to the UI thread before touching UI.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog("System resumed from sleep — refreshing.");
                if (_activeDownloads == 0 && !_batchActive)
                {
                    _lastKnownRuleCount = FetchRuleCountSafe();
                    RefreshAllGrids();
                }
            }));
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_minimizeToTray && !_exitRequested)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            SaveLayout();
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _tray?.Dispose();
            Application.Current.Shutdown();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            UpdateMaxRestoreButton();

            if (WindowState == System.Windows.WindowState.Minimized && _minimizeToTray)
                Hide();
        }

        // ── Window menu ───────────────────────────────────────────────────────

        private void UpdateMaxRestoreButton()
        {
            bool max = WindowState == System.Windows.WindowState.Maximized;
            BtnMaxRestore.ToolTip = max ? "Wiederherstellen" : "Maximieren";
            BtnMaxRestore.ApplyTemplate();
            if (BtnMaxRestore.Template.FindName("Ic", BtnMaxRestore) is TextBlock ic)
                ic.Text = max ? "" : "";
        }

        private void MenuItemMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = System.Windows.WindowState.Minimized;

        private void MenuItemMaxRestore_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == System.Windows.WindowState.Maximized
                ? System.Windows.WindowState.Normal
                : System.Windows.WindowState.Maximized;

        private void MenuItemClose_Click(object sender, RoutedEventArgs e)
            => Close();

        // ── Timers ────────────────────────────────────────────────────────────

        private void Timer_RuleChangeCheck_Tick(object sender, EventArgs e)
        {
            if (_activeDownloads > 0 || _batchActive) return;

            int current = FetchRuleCountSafe();
            if (current == _lastKnownRuleCount) return;
            _lastKnownRuleCount = current;
            RefreshAllGrids();
        }

        private void Timer_PeriodicRefresh_Tick(object sender, EventArgs e)
        {
            if (_activeDownloads > 0 || _batchActive) return;
            RefreshAllGrids();
        }

        private void Timer_RemoveFinishedTorrents_Tick(object sender, EventArgs e)
            => RemoveFinishedTorrents();

        private async void Timer_ProgressPoll_Tick(object sender, EventArgs e)
        {
            if (_recognizedFiles == null) return;
            var tracked = _recognizedFiles.Where(f => f.IsInTransmission).ToList();
            if (tracked.Count == 0) return;

            try
            {
                var active     = await Task.Run(() => _transmission.GetActiveTorrents());
                var activeById = active.ToDictionary(t => t.Id);
                var completed  = new List<(RecognizedFile File, TorrentProgress Torrent)>();

                foreach (var file in tracked)
                {
                    if (activeById.TryGetValue(file.TorrentId, out var progress))
                    {
                        file.Progress           = progress.PercentDone;
                        file.DownloadSpeedBytes = progress.RateDownload;
                        file.DownloadEta        = progress.Eta;

                        if (progress.PercentDone >= 1.0)
                            completed.Add((file, progress));
                    }
                    else
                    {
                        // Not found → assume Transmission removed it (done or user removed it)
                        completed.Add((file, null));
                    }
                }

                foreach (var (file, torrent) in completed)
                {
                    _recognizedFiles.Remove(file);
                    AppendLog($"'{file.Filename}' download complete.");
                    ShowNotification("Download Complete", file.Filename);

                    // The row left the list — remove the torrent from Transmission too
                    // (torrent == null means it is already gone from Transmission).
                    if (torrent == null) continue;
                    try
                    {
                        await Task.Run(() => _transmission.RemoveTorrent(torrent.Id));
                        AppendLog($"Torrent '{torrent.Name}' removed.");
                        await Task.Run(() => _api.NotifyTorrentRemoved(torrent.Name));
                    }
                    catch
                    {
                        AppendLog($"Warning: Could not remove torrent '{torrent.Name}' from Transmission.");
                    }
                }

                if (completed.Count > 0)
                {
                    UpdateItemCount();
                    UpdateTrayIndicators();
                    if (_recognizedFiles.Count == 0)
                        SetPanelState(GridDownload, PanelDownloadEmpty, PanelDownloadError,
                                      hasData: false, isError: false);
                }
            }
            catch { /* Transmission unreachable — leave in-progress rows as-is */ }
        }

        // ── Data loading ──────────────────────────────────────────────────────

        private void RefreshAllGrids()
        {
            LoadUnrecognizedFiles();
            LoadRecognizedFiles();
        }

        private void LoadUnrecognizedFiles()
        {
            try
            {
                _unrecognizedFiles = new ObservableCollection<UnrecognizedFile>(
                    _api.GetUnrecognizedFiles());

                var view = CollectionViewSource.GetDefaultView(_unrecognizedFiles);
                view.Filter = o =>
                {
                    if (string.IsNullOrEmpty(_searchUnrecognized)) return true;
                    var f = o as UnrecognizedFile;
                    return f?.Name?.IndexOf(_searchUnrecognized, StringComparison.OrdinalIgnoreCase) >= 0;
                };
                GridUnrecognized.ItemsSource = view;

                SetPanelState(GridUnrecognized, PanelUnrecognizedEmpty, PanelUnrecognizedError,
                              hasData: _unrecognizedFiles.Count > 0, isError: false);
                UpdateTrayIndicators();
            }
            catch
            {
                SetPanelState(GridUnrecognized, PanelUnrecognizedEmpty, PanelUnrecognizedError,
                              hasData: false, isError: true);
                AppendLog("Error: Unable to load unrecognized files.");
            }
        }

        private void LoadRecognizedFiles()
        {
            // Preserve rows currently tracked inside Transmission (they're not in the backend list)
            var inTransmission = _recognizedFiles?.Where(f => f.IsInTransmission).ToList()
                                 ?? new List<RecognizedFile>();

            // Capture current sort before ItemsSource is replaced (replacement clears it)
            var savedSorts      = GridDownload.Items.SortDescriptions.ToList();
            var savedDirections = GridDownload.Columns.Select(c => c.SortDirection).ToList();

            try
            {
                _recognizedFiles = new ObservableCollection<RecognizedFile>(
                    _api.GetRecognizedFiles());

                // Re-add in-transmission rows that the backend no longer lists
                foreach (var f in inTransmission)
                {
                    if (!_recognizedFiles.Any(r => r.Id == f.Id))
                        _recognizedFiles.Insert(0, f);
                }

                var view = CollectionViewSource.GetDefaultView(_recognizedFiles);
                view.Filter = o =>
                {
                    if (string.IsNullOrEmpty(_searchDownload)) return true;
                    var f = o as RecognizedFile;
                    return f?.Filename?.IndexOf(_searchDownload, StringComparison.OrdinalIgnoreCase) >= 0;
                };
                GridDownload.ItemsSource = view;

                // Re-apply the sort the user had before the refresh
                foreach (var sort in savedSorts)
                    GridDownload.Items.SortDescriptions.Add(sort);
                for (int i = 0; i < GridDownload.Columns.Count && i < savedDirections.Count; i++)
                    GridDownload.Columns[i].SortDirection = savedDirections[i];

                UpdateItemCount();
                SetPanelState(GridDownload, PanelDownloadEmpty, PanelDownloadError,
                              hasData: _recognizedFiles.Count > 0, isError: false);
                UpdateTrayIndicators();
                AppendLog("Recognized files refreshed.");
            }
            catch
            {
                GridDownload.ItemsSource = null;
                SetPanelState(GridDownload, PanelDownloadEmpty, PanelDownloadError,
                              hasData: false, isError: true);
                AppendLog("Error: Unable to load recognized files.");
            }
        }

        /// <summary>
        /// Applies a persisted sort from settings. Called once after the first data load.
        /// </summary>
        private void RestoreSort(AppSettings settings)
        {
            if (string.IsNullOrEmpty(settings.SortColumnPath)) return;

            var dir = settings.SortAscending
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;

            GridDownload.Items.SortDescriptions.Clear();
            GridDownload.Items.SortDescriptions.Add(new SortDescription(settings.SortColumnPath, dir));

            foreach (var col in GridDownload.Columns)
            {
                string path = GetColumnSortPath(col);
                col.SortDirection = path == settings.SortColumnPath ? dir : (ListSortDirection?)null;
            }
        }

        /// <summary>Returns the effective sort-member path for any DataGrid column type.</summary>
        private static string GetColumnSortPath(DataGridColumn col)
        {
            if (!string.IsNullOrEmpty(col.SortMemberPath)) return col.SortMemberPath;
            if (col is DataGridBoundColumn bound)
                return (bound.Binding as Binding)?.Path?.Path;
            return null;
        }

        private int FetchRuleCountSafe()
        {
            try { return _api.GetRuleCount(); }
            catch
            {
                AppendLog("Error: Unable to fetch rule count.");
                return _lastKnownRuleCount;
            }
        }

        // ── Torrent management ────────────────────────────────────────────────

        private void RemoveFinishedTorrents()
        {
            try
            {
                foreach (string name in _transmission.RemoveFinishedTorrents())
                {
                    AppendLog($"Torrent '{name}' removed.");
                    _api.NotifyTorrentRemoved(name);
                }
            }
            catch
            {
                AppendLog("Error: Could not reach Transmission. Is it running?");
            }
        }

        /// <summary>
        /// Hands <paramref name="file"/> off to Transmission asynchronously so the UI
        /// stays responsive. Shows a pulsing "Adding…" button, then removes the row on
        /// success with a brief green flash.
        /// </summary>
        private async Task DownloadFileAsync(RecognizedFile file)
        {
            if (file.IsDownloading || file.IsAdded || file.IsInTransmission) return;

            _activeDownloads++;
            file.IsDownloading = true;
            BtnDownloadAll.IsEnabled = false;

            try
            {
                int torrentId = await Task.Run(() =>
                    _transmission.AddAndStartTorrent(file, _torrentFileDirectory));

                if (torrentId > 0)
                {
                    AppendLog($"Added '{file.Filename}' to Transmission (id {torrentId}).");
                    await Task.Run(() => _api.MarkAsDownloaded(Convert.ToInt32(file.Id)));

                    // Brief green flash, then hand off to the progress-polling timer
                    file.IsAdded = true;
                    await Task.Delay(600);
                    file.IsAdded        = false;
                    file.IsDownloading  = false;
                    file.TorrentId      = torrentId;
                    file.Progress       = 0;
                    file.IsInTransmission = true;
                    file.RetryCount     = 0;

                    ShowNotification("Download Started", file.Filename);
                }
                else
                {
                    file.IsDownloading = false;
                    AppendLog($"Warning: Transmission returned no ID for '{file.Filename}'.");
                }
            }
            catch (Exception ex)
            {
                file.IsDownloading = false;
                AppendLog($"Error adding '{file.Filename}': {ex.Message}");
                ShowNotification("Download Error", file.Filename,
                                 System.Windows.Forms.ToolTipIcon.Warning);

                // Auto-retry
                if (_autoRetryEnabled && file.RetryCount < 3)
                {
                    file.RetryCount++;
                    int attempt = file.RetryCount;
                    AppendLog($"Will retry '{file.Filename}' in {_autoRetryDelayMs / 1000}s ({attempt}/3)…");
                    _ = Task.Delay(_autoRetryDelayMs).ContinueWith(_ =>
                        Dispatcher.InvokeAsync(async () => await DownloadFileAsync(file)));
                }
            }
            finally
            {
                _activeDownloads--;
                if (_activeDownloads == 0 && !_batchActive)
                    BtnDownloadAll.IsEnabled = true;
            }
        }

        private async Task DownloadAllAsync()
        {
            if (_activeDownloads > 0 || _batchActive) return;

            _batchActive = true;
            BtnDownloadAll.IsEnabled = false;

            // Snapshot in current display order (respects the user's active sort)
            var snapshot = GridDownload.Items.OfType<RecognizedFile>().ToList();
            foreach (var f in snapshot) f.IsLocked = true;

            try
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var file = snapshot[i];
                    if (!_recognizedFiles.Contains(file)) continue;

                    await DownloadFileAsync(file);

                    // Countdown between adds; skip after the last item
                    if (i < snapshot.Count - 1 && _batchDelayMs > 0)
                    {
                        int totalSec = _batchDelayMs / 1000;
                        for (int rem = totalSec; rem > 0; rem--)
                        {
                            TxtBatchStatus.Text =
                                $"{LocalizationManager.Get("TxtNextIn")} {FormatRemainingTime(rem)}";
                            TxtBatchStatus.Visibility = Visibility.Visible;
                            await Task.Delay(1000);
                        }
                        // Sub-second fractional remainder
                        int rest = _batchDelayMs % 1000;
                        if (rest > 0) await Task.Delay(rest);

                        TxtBatchStatus.Visibility = Visibility.Collapsed;
                    }
                }
            }
            finally
            {
                TxtBatchStatus.Visibility = Visibility.Collapsed;
                _batchActive = false;
                foreach (var f in _recognizedFiles) f.IsLocked = false;
                if (_activeDownloads == 0)
                    BtnDownloadAll.IsEnabled = true;
            }
        }

        private static string FormatEta(TimeSpan t)
        {
            int s = (int)Math.Ceiling(t.TotalSeconds);
            if (s <= 0)  return "0s";
            if (s < 60)  return $"{s}s";
            if (s < 3600) return $"{s / 60}m {s % 60:D2}s";
            return $"{s / 3600}h {(s % 3600) / 60:D2}m";
        }

        private static string FormatRemainingTime(int totalSec)
        {
            if (totalSec >= 3600)
            {
                int h = totalSec / 3600;
                int m = (totalSec % 3600) / 60;
                int s = totalSec % 60;
                if (m == 0 && s == 0) return $"{h}h";
                if (s == 0) return $"{h}h {m:D2}m";
                return $"{h}h {m:D2}m {s:D2}s";
            }
            if (totalSec >= 60)
            {
                int m = totalSec / 60;
                int s = totalSec % 60;
                return s > 0 ? $"{m}m {s:D2}s" : $"{m}m";
            }
            return $"{totalSec}s";
        }

        // ── Delay ComboBox ────────────────────────────────────────────────────

        private void PopulateDelayComboBox()
        {
            _populatingDelay = true;
            try
            {
                CboDelay.Items.Clear();
                int selectedIdx = 3; // default: 5 s
                for (int i = 0; i < _delayMs.Length; i++)
                {
                    CboDelay.Items.Add(FormatDelayLabel(_delayMs[i]));
                    if (_delayMs[i] == _batchDelayMs) selectedIdx = i;
                }
                CboDelay.SelectedIndex = selectedIdx;
                TxtDelayIndicator.Text = FormatDelayLabel(_batchDelayMs);
            }
            finally
            {
                _populatingDelay = false;
            }
        }

        private string FormatDelayLabel(int ms)
        {
            if (ms == 0)      return LocalizationManager.Get("DelayImmediate");
            if (ms < 60000)   return $"{ms / 1000} s";
            if (ms < 3600000) return $"{ms / 60000} min";
            return $"{ms / 3600000} h";
        }

        private void CboDelay_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_populatingDelay) return;
            if (CboDelay.SelectedIndex < 0 || CboDelay.SelectedIndex >= _delayMs.Length) return;

            _batchDelayMs = _delayMs[CboDelay.SelectedIndex];
            TxtDelayIndicator.Text = FormatDelayLabel(_batchDelayMs);

            SettingsStorage.Update(s => s.BatchDelayMs = _batchDelayMs);
        }

        // ── Panel maximize / restore ──────────────────────────────────────────

        private const int CollapsedPanelWidth = 60;

        private void BtnMaxLeft_Click(object sender, RoutedEventArgs e)
            => MaximizePanel(PanelLayout.LeftFull);

        private void BtnMaxRight_Click(object sender, RoutedEventArgs e)
            => MaximizePanel(PanelLayout.RightFull);

        private void MaximizePanel(PanelLayout target)
        {
            // Clicking the button of the already-maximized panel restores normal layout
            if (_panelLayout == target)
            {
                RestorePanels();
                return;
            }

            // Save proportions only once, from a clean Normal state.
            // When switching directly between LeftFull and RightFull we reuse the
            // proportions that were saved when we first left Normal, so they stay correct.
            if (_panelLayout == PanelLayout.Normal)
                SavePanelProportions();

            // Clear any strip visuals from the previous maximize (if switching states)
            ClearMaximizeVisuals();

            _panelLayout = target;

            if (target == PanelLayout.LeftFull)
            {
                // Left panel fills available width; right panel becomes a narrow strip
                GridPanels.ColumnDefinitions[0].MinWidth = 200;
                GridPanels.ColumnDefinitions[0].Width    = new GridLength(1, GridUnitType.Star);
                GridPanels.ColumnDefinitions[1].Width    = new GridLength(0);
                GridPanels.ColumnDefinitions[2].MinWidth = 0;
                GridPanels.ColumnDefinitions[2].Width    = new GridLength(CollapsedPanelWidth);

                SplitterPanels.Visibility             = Visibility.Collapsed;
                StackHeaderDownload.Visibility        = Visibility.Collapsed;
                PanelRightCollapsedOverlay.Visibility = Visibility.Visible;
                BtnMaxLeft.Content  = "⤡";
                BtnMaxRight.Content = "⤢";
            }
            else // RightFull
            {
                // Right panel fills available width; left panel becomes a narrow strip
                GridPanels.ColumnDefinitions[0].MinWidth = 0;
                GridPanels.ColumnDefinitions[0].Width    = new GridLength(CollapsedPanelWidth);
                GridPanels.ColumnDefinitions[1].Width    = new GridLength(0);
                GridPanels.ColumnDefinitions[2].MinWidth = 200;
                GridPanels.ColumnDefinitions[2].Width    = new GridLength(1, GridUnitType.Star);

                SplitterPanels.Visibility            = Visibility.Collapsed;
                TxtHeaderUnrecognized.Visibility     = Visibility.Collapsed;
                PanelLeftCollapsedOverlay.Visibility = Visibility.Visible;
                BtnMaxLeft.Content  = "⤢";
                BtnMaxRight.Content = "⤡";
            }
        }

        // Hides all strip-state visuals without touching column widths or _panelLayout.
        private void ClearMaximizeVisuals()
        {
            PanelLeftCollapsedOverlay.Visibility  = Visibility.Collapsed;
            PanelRightCollapsedOverlay.Visibility = Visibility.Collapsed;
            TxtHeaderUnrecognized.Visibility      = Visibility.Visible;
            StackHeaderDownload.Visibility        = Visibility.Visible;
        }

        private void PanelCollapsedOverlay_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => RestorePanels();

        private void RestorePanels()
        {
            ClearMaximizeVisuals();

            GridPanels.ColumnDefinitions[0].MinWidth = 200;
            GridPanels.ColumnDefinitions[2].MinWidth = 200;
            GridPanels.ColumnDefinitions[0].Width    = new GridLength(_savedLeftStars,  GridUnitType.Star);
            GridPanels.ColumnDefinitions[1].Width    = new GridLength(5);
            GridPanels.ColumnDefinitions[2].Width    = new GridLength(_savedRightStars, GridUnitType.Star);

            SplitterPanels.Visibility = Visibility.Visible;
            BtnMaxLeft.Content        = "⤢";
            BtnMaxRight.Content       = "⤢";
            _panelLayout              = PanelLayout.Normal;
        }

        private void SavePanelProportions()
        {
            double lw    = GridPanels.ColumnDefinitions[0].ActualWidth;
            double rw    = GridPanels.ColumnDefinitions[2].ActualWidth;
            double total = lw + rw;
            _savedLeftStars  = total > 0 ? (lw / total) * 3 : 1;
            _savedRightStars = total > 0 ? (rw / total) * 3 : 2;
        }

        // ── Event log ─────────────────────────────────────────────────────────

        private void BtnLogToggle_Click(object sender, RoutedEventArgs e)
        {
            _logExpanded = !_logExpanded;
            GridMain.RowDefinitions[2].Height = _logExpanded
                ? new GridLength(148)
                : new GridLength(32);
            BtnLogToggle.Content = _logExpanded ? "▼" : "▲";
        }

        private void AppendLog(string message)
        {
            var color = message.StartsWith("Error")   ? Color.FromRgb(0xF4, 0x47, 0x47)
                      : message.StartsWith("Warning") ? Color.FromRgb(0xFC, 0xC4, 0x1F)
                      :                                 Color.FromRgb(0x9C, 0xDC, 0xFE);
            AppendLog(message, color);
        }

        private void AppendLog(string message, Color color)
        {
            bool nearBottom = IsLogScrolledToBottom();

            var para = new Paragraph(
                new Run($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} — {message}"))
            {
                Margin     = new Thickness(0),
                Foreground = new SolidColorBrush(color)
            };
            TxtEventLog.Document.Blocks.Add(para);

            // Keep the log bounded so a long/verbose session (e.g. a big duplicate scan)
            // can't grow the RichTextBox until scrolling and layout crawl.
            var blocks = TxtEventLog.Document.Blocks;
            while (blocks.Count > 2000)
                blocks.Remove(blocks.FirstBlock);

            if (nearBottom)
                TxtEventLog.ScrollToEnd();
        }

        private bool IsLogScrolledToBottom()
        {
            if (_logScrollViewer == null) return true;
            return _logScrollViewer.ScrollableHeight <= 0
                || _logScrollViewer.VerticalOffset >= _logScrollViewer.ScrollableHeight - 20;
        }

        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv) return sv;
                var found = FindScrollViewer(child);
                if (found != null) return found;
            }
            return null;
        }

        // Returns false quickly (≤2 s) if the UNC server is not reachable on port 445 (SMB).
        // Uses truly async overlapped I/O so no thread is ever blocked.
        // Non-UNC paths (local, mapped drives) are assumed reachable and return true immediately.
        private static async Task<bool> IsUncPathReachableAsync(string path)
        {
            if (!path.StartsWith(@"\\")) return true;

            // Extract server name from \\server\share\...
            string trimmed = path.TrimStart('\\');
            int slash = trimmed.IndexOf('\\');
            string server = slash >= 0 ? trimmed.Substring(0, slash) : trimmed;
            if (string.IsNullOrEmpty(server)) return true;

            try
            {
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    var connectTask = tcp.ConnectAsync(server, 445);
                    // Timeout of 2 s — either the server responds or it doesn't
                    if (await Task.WhenAny(connectTask, Task.Delay(2000)) != connectTask)
                        return false; // timed out
                    return !connectTask.IsFaulted; // connection refused still means server is up
                }
            }
            catch { return false; }
        }

        private static T FindVisualChildByName<T>(DependencyObject parent, string name)
            where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T fe && fe.Name == name) return fe;
                var found = FindVisualChildByName<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private static void SetPanelState(
            FrameworkElement dataGrid,
            FrameworkElement emptyPanel,
            FrameworkElement errorPanel,
            bool hasData, bool isError)
        {
            dataGrid.Visibility   = hasData                ? Visibility.Visible : Visibility.Collapsed;
            emptyPanel.Visibility = (!isError && !hasData) ? Visibility.Visible : Visibility.Collapsed;
            errorPanel.Visibility = isError                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateItemCount()
        {
            if (_recognizedFiles == null) return;
            int count = _recognizedFiles.Count;
            string unit = count == 1
                ? LocalizationManager.Get("Item")
                : LocalizationManager.Get("Items");
            LblItemCount.Content = $"{count} {unit}";
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private void BtnSetRule_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = (UnrecognizedFile)GridUnrecognized.SelectedItem;
                Uri apiUri = new Uri(_apiBaseUrl);
                string url = $"{apiUri.Scheme}://{apiUri.Host}/manage-rules?sourceEntryId={selected.Id}";
                System.Diagnostics.Process.Start(url);
            }
            catch
            {
                MessageBox.Show("Please select a row first.");
            }
        }

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            var file = (sender as FrameworkElement)?.DataContext as RecognizedFile;
            if (file != null)
                await DownloadFileAsync(file);
        }

        private async void BtnDeleteEntry_Click(object sender, RoutedEventArgs e)
        {
            var file = (sender as FrameworkElement)?.DataContext as RecognizedFile;
            if (file == null || file.IsDownloading || file.IsInTransmission) return;

            var dlg = new ConfirmDeleteWindow(file.Filename) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            try
            {
                await Task.Run(() => _api.DeleteEntry(Convert.ToInt32(file.Id)));
                _recognizedFiles.Remove(file);
                UpdateItemCount();
                SetPanelState(GridDownload, PanelDownloadEmpty, PanelDownloadError,
                              hasData: _recognizedFiles.Count > 0, isError: false);
                UpdateTrayIndicators();
                AppendLog($"Deleted entry '{file.Filename}'.");
            }
            catch (WebException ex) when ((ex.Response as HttpWebResponse) != null)
            {
                switch ((int)((HttpWebResponse)ex.Response).StatusCode)
                {
                    case 403: AppendLog($"Error: Deleting '{file.Filename}' requires admin rights."); break;
                    case 404: AppendLog($"Error: Entry '{file.Filename}' no longer exists."); break;
                    default:  AppendLog($"Error deleting '{file.Filename}': {ex.Message}"); break;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error deleting '{file.Filename}': {ex.Message}");
            }
        }

        private async void BtnDownloadAll_Click(object sender, RoutedEventArgs e)
            => await DownloadAllAsync();

        private void BtnResumeAll_Click(object sender, RoutedEventArgs e)
        {
            try { _transmission.StartAll(); }
            catch { AppendLog("Error: Could not resume downloads."); }
        }

        private void BtnPauseAll_Click(object sender, RoutedEventArgs e)
        {
            try { _transmission.StopAll(); }
            catch { AppendLog("Error: Could not pause downloads."); }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var prevLanguage = LocalizationManager.Current;
            var win = new SettingsWindow();

            if (win.ShowDialog() != true)
            {
                LocalizationManager.SetLanguage(prevLanguage);
                ApplyLanguage();
                return;
            }

            if (win.ResetLayoutRequested)
                ApplyDefaultLayout();

            var settings = SettingsStorage.Load();
            ApplySettings(settings);
            LocalizationManager.SetLanguage(settings.Language ?? "English");
            ApplyLanguage();

            // Sync autostart in case it was toggled
            SettingsWindow.ApplyAutostart(settings.StartWithWindows);

            AppendLog("Settings updated.");
        }

        private void ApplyDefaultLayout()
        {
            Width  = 1280;
            Height = 720;
            Left   = (SystemParameters.WorkArea.Width  - Width)  / 2;
            Top    = (SystemParameters.WorkArea.Height - Height) / 2;

            _logExpanded = true;
            GridMain.RowDefinitions[2].Height = new GridLength(148);
            BtnLogToggle.Content = "▼";

            RestorePanels();
        }

        // ── Navigation ────────────────────────────────────────────────────────

        private void BtnNavDownloadManager_Click(object sender, RoutedEventArgs e)
            => SwitchToView(AppView.DownloadManager);

        private void BtnNavLibraryManager_Click(object sender, RoutedEventArgs e)
            => SwitchToView(AppView.LibraryManager);

        private void BtnNavDuplicateFinder_Click(object sender, RoutedEventArgs e)
            => SwitchToView(AppView.DuplicateFinder);

        private void SwitchToView(AppView view)
        {
            _currentView = view;
            bool dl  = view == AppView.DownloadManager;
            bool lib = view == AppView.LibraryManager;
            bool dup = view == AppView.DuplicateFinder;

            ViewDownloadManager.Visibility = dl  ? Visibility.Visible : Visibility.Collapsed;
            ViewLibraryManager.Visibility  = lib ? Visibility.Visible : Visibility.Collapsed;
            ViewDuplicateFinder.Visibility = dup ? Visibility.Visible : Visibility.Collapsed;

            BorderNavDlActive.Visibility  = dl  ? Visibility.Visible : Visibility.Collapsed;
            BorderNavLibActive.Visibility = lib ? Visibility.Visible : Visibility.Collapsed;
            BorderNavDupActive.Visibility = dup ? Visibility.Visible : Visibility.Collapsed;

            var activeColor   = new SolidColorBrush(Color.FromRgb(0x64, 0x76, 0x87)); // Steel #647687
            var inactiveColor = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

            BtnNavDownloadManager.Foreground = dl  ? activeColor : inactiveColor;
            BtnNavLibraryManager.Foreground  = lib ? activeColor : inactiveColor;
            BtnNavDuplicateFinder.Foreground = dup ? activeColor : inactiveColor;

            // Show the event log everywhere except Library Manager, which needs the space.
            bool showLog = !lib;
            LogRow.Height = showLog ? new GridLength(_logExpanded ? 148 : 32) : new GridLength(0);
            BorderEventLog.Visibility = showLog ? Visibility.Visible : Visibility.Collapsed;

            // Auto-scan the library on the first visit so the loading indicator appears immediately
            if (lib && _libraryFirstVisit)
            {
                _libraryFirstVisit = false;
                _ = LibraryScanAsync();
            }
        }

        // ── Library Manager — setup ───────────────────────────────────────────

        private void LoadLibrarySettings(AppSettings settings)
        {
            _watchedFolders = new ObservableCollection<WatchedFolderItem>(
                (settings.WatchedFolders ?? new List<string>()).Select(p => new WatchedFolderItem(p)));
            _libraryRules = new ObservableCollection<LibraryRule>(
                settings.LibraryRules ?? new List<LibraryRule>());
            _scanResults = new ObservableCollection<ScanResult>();
            _libraryCache = LibraryFileCache.Load();

            LstWatchedFolders.ItemsSource = _watchedFolders;
            GridRules.ItemsSource         = _libraryRules;

            _scanViewSource        = (CollectionViewSource)FindResource("ScanResultsView");
            _scanViewSource.Source = _scanResults;
            _scanViewSource.Filter += ScanResultsView_Filter;

            // Duplicate Finder
            _dupFolders = new ObservableCollection<DuplicateFolderItem>(
                (settings.DuplicateFolders ?? new List<string>()).Select(p => new DuplicateFolderItem(p)));
            _dupGroups    = new ObservableCollection<DuplicateGroup>();
            _dupHashCache = DuplicateHashCache.Load();
            _dupIgnored   = new HashSet<string>(
                settings.DuplicateIgnoredHashes ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            LstDupFolders.ItemsSource   = _dupFolders;
            GridDuplicates.ItemsSource  = _dupGroups;
        }

        private void SaveDuplicateFolders()
            => SettingsStorage.Update(s => s.DuplicateFolders = _dupFolders.Select(f => f.Path).ToList());

        private void SaveDuplicateIgnored()
            => SettingsStorage.Update(s => s.DuplicateIgnoredHashes = _dupIgnored.ToList());

        // ── Duplicate Finder — folder management ──────────────────────────────

        private static readonly Color _dupKeepColor   = Color.FromRgb(0x57, 0xC4, 0x6E);
        private static readonly Color _dupDeleteColor = Color.FromRgb(0xF4, 0x47, 0x47);

        private void BtnDupAddFolder_Click(object sender, RoutedEventArgs e)
        {
            string picked = FolderPicker.Pick(title: "Select a folder to scan for duplicates");
            if (picked != null &&
                !_dupFolders.Any(f => f.Path.Equals(picked, StringComparison.OrdinalIgnoreCase)))
            {
                _dupFolders.Add(new DuplicateFolderItem(picked));
                SaveDuplicateFolders();
            }
        }

        private void BtnDupRemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(LstDupFolders.SelectedItem is DuplicateFolderItem sel)) return;

            _dupFolders.Remove(sel);
            SaveDuplicateFolders();

            // Drop any already-found copies that live under the removed folder so they are
            // no longer shown or considered, then re-evaluate the surviving groups.
            string prefix = sel.Path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var g in _dupGroups.ToList())
            {
                var gone = g.Files.Where(f =>
                        f.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        f.FullPath.Equals(sel.Path, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (gone.Count == 0) continue;

                foreach (var f in gone) g.Files.Remove(f);
                if (g.Files.Count < 2) _dupGroups.Remove(g);   // no duplicate left to track
            }

            RecomputeFolderOrders();   // re-map priorities + re-apply defaults on survivors

            if (_dupGroups.Count == 0)
            {
                TxtDupEmpty.Text       = "No duplicate groups to show.";
                TxtDupEmpty.Visibility = Visibility.Visible;
            }
            UpdateDupCounts();
            AppendLog($"Removed folder from Duplicate Finder: {sel.Path}");
        }

        // ── Duplicate Finder — drag-to-reorder ────────────────────────────────

        private void LstDupFolders_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dupDragStart = e.GetPosition(null);
            _dupDragItem  = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)
                                ?.DataContext as DuplicateFolderItem;
        }

        private void LstDupFolders_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dupDragItem == null) return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dupDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dupDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            DragDrop.DoDragDrop(LstDupFolders, _dupDragItem, DragDropEffects.Move);
        }

        private void LstDupFolders_Drop(object sender, DragEventArgs e)
        {
            var dragged = _dupDragItem;
            _dupDragItem = null;
            if (dragged == null) return;

            var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)
                             ?.DataContext as DuplicateFolderItem;

            int oldIndex = _dupFolders.IndexOf(dragged);
            int newIndex = target != null ? _dupFolders.IndexOf(target) : _dupFolders.Count - 1;
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;

            _dupFolders.Move(oldIndex, newIndex);
            SaveDuplicateFolders();
            RecomputeFolderOrders();
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t) return t;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // Re-maps each found file to its folder's new priority and re-applies the default
        // keep/delete decision (topmost folder keeps). Called after the folder order changes.
        private void RecomputeFolderOrders()
        {
            if (_dupGroups == null || _dupGroups.Count == 0) return;
            foreach (var g in _dupGroups)
            {
                foreach (var f in g.Files)
                    f.FolderOrder = OrderForPath(f.FullPath);
                g.ApplyDefaultKeep();
            }
            UpdateDupCounts();
        }

        private int OrderForPath(string path)
        {
            for (int i = 0; i < _dupFolders.Count; i++)
            {
                string root = _dupFolders[i].Path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return int.MaxValue;
        }

        // ── Duplicate Finder — scan ───────────────────────────────────────────

        private async void BtnFindDuplicates_Click(object sender, RoutedEventArgs e)
        {
            if (_dupScanning) { _dupCts?.Cancel(); return; }
            await FindDuplicatesAsync();
        }

        private async Task FindDuplicatesAsync()
        {
            // Reachability pre-check (mirrors the Library Manager's UNC-safe approach).
            var folders = new List<string>();
            foreach (var item in _dupFolders)
            {
                bool ok = await IsUncPathReachableAsync(item.Path)
                          && (PathHelper.IsNetworkPath(item.Path) || Directory.Exists(item.Path));
                item.IsAvailable = ok;
                if (ok) folders.Add(item.Path);
                else    AppendLog($"Warning: Folder not reachable (skipped): {item.Path}");
            }

            if (folders.Count == 0)
            {
                AppendLog("Warning: No reachable folders to scan for duplicates.");
                return;
            }

            _dupScanning = true;
            _dupCts      = new CancellationTokenSource();
            BtnFindDuplicates.Content   = "■  Cancel";
            BtnDupAddFolder.IsEnabled    = false;
            BtnDupRemoveFolder.IsEnabled = false;
            BtnDupCleanupSelected.IsEnabled = false;
            DupProgressRow.Visibility   = Visibility.Visible;
            DupProgressBar.Value        = 0;
            TxtDupProgressFile.Text     = "";
            TxtDupProgressStat.Text     = "";
            TxtDupEmpty.Visibility      = Visibility.Collapsed;
            _dupGroups.Clear();
            UpdateDupCounts();

            var progress = new Progress<DuplicateFinder.ScanProgress>(p =>
            {
                if (p.Fraction > 0 || p.FilesTotal > 0)
                    DupProgressBar.Value = p.Fraction;
                if (p.FilesTotal > 0)
                    TxtDupProgressStat.Text = $"{p.FilesProcessed} / {p.FilesTotal}";
                if (!string.IsNullOrEmpty(p.Phase) || !string.IsNullOrEmpty(p.CurrentFile))
                    TxtDupProgressFile.Text = string.IsNullOrEmpty(p.CurrentFile)
                        ? p.Phase
                        : $"{p.Phase}   {p.CurrentFile}";
                if (!string.IsNullOrEmpty(p.LogMessage))
                    AppendLog(p.LogMessage);
            });

            AppendLog($"Scanning {folders.Count} folder(s) for duplicates…");
            try
            {
                var groups = await DuplicateFinder.FindAsync(folders, _dupHashCache, progress, _dupCts.Token);
                _dupHashCache.Save();

                int ignoredCount = groups.Count(g => _dupIgnored.Contains(g.Hash));
                groups = groups.Where(g => !_dupIgnored.Contains(g.Hash)).ToList();
                foreach (var g in groups) _dupGroups.Add(g);

                long reclaim = groups.Sum(g => g.ReclaimableBytes);
                string ignoredNote = ignoredCount > 0 ? $" ({ignoredCount} ignored)" : "";
                AppendLog($"Found {groups.Count} duplicate group(s){ignoredNote}; up to " +
                          $"{DuplicateGroup.FormatBytes(reclaim)} reclaimable.");
            }
            catch (OperationCanceledException)
            {
                AppendLog("Warning: Duplicate scan cancelled.");
                _dupHashCache.Save();
            }
            catch (Exception ex)
            {
                AppendLog($"Error: Duplicate scan failed: {ex.Message}");
            }
            finally
            {
                _dupScanning = false;
                _dupCts?.Dispose();
                _dupCts = null;
                BtnFindDuplicates.Content    = "⚙  Find Duplicates";
                BtnDupAddFolder.IsEnabled     = true;
                BtnDupRemoveFolder.IsEnabled  = true;
                UpdateDupCleanupSelectedEnabled();
                DupProgressRow.Visibility    = Visibility.Collapsed;

                if (_dupGroups.Count == 0)
                {
                    TxtDupEmpty.Text       = "No duplicates found — every file is unique. 🎉";
                    TxtDupEmpty.Visibility = Visibility.Visible;
                }
                UpdateDupCounts();
            }
        }

        private void GridDuplicates_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateDupCleanupSelectedEnabled();

        // Cleanup Selected is only usable once at least one group is selected and no scan
        // is running.
        private void UpdateDupCleanupSelectedEnabled()
            => BtnDupCleanupSelected.IsEnabled = !_dupScanning && GridDuplicates.SelectedItems.Count > 0;

        private void UpdateDupCounts()
        {
            if (_dupGroups == null) return;
            int groups   = _dupGroups.Count;
            int files    = _dupGroups.Sum(g => g.CopyCount);
            long reclaim = _dupGroups.Sum(g => g.ReclaimableBytes);

            LblDupCount.Content    = groups == 1 ? "1 group" : $"{groups} groups";
            TxtDupReclaimable.Text = reclaim > 0 ? $"↓ {DuplicateGroup.FormatBytes(reclaim)} reclaimable" : "";
            TxtDupSummary.Text     = groups == 0
                ? ""
                : $"{groups} group(s) · {files} files · up to {DuplicateGroup.FormatBytes(reclaim)} reclaimable if cleaned up";
        }

        // ── Duplicate Finder — cleanup / resolve ──────────────────────────────

        private IList<DuplicateGroup> GetSelectedDupGroups()
            => GridDuplicates.SelectedItems.Cast<DuplicateGroup>().ToList();

        private async void BtnDupCleanupOne_Click(object sender, RoutedEventArgs e)
        {
            var g = (sender as FrameworkElement)?.DataContext as DuplicateGroup;
            if (g != null) await CleanupWithPreviewAsync(new List<DuplicateGroup> { g });
        }

        private async void BtnDupCleanupSelected_Click(object sender, RoutedEventArgs e)
            => await CleanupWithPreviewAsync(GetSelectedDupGroups());

        private async void MenuItemDupCleanupSelected_Click(object sender, RoutedEventArgs e)
            => await CleanupWithPreviewAsync(GetSelectedDupGroups());

        // Routes cleanup through a warning/preview modal whenever any group is undecided
        // (all copies in the same folder → no default winner). The modal applies the
        // "keep the first file, delete the rest" fallback and shows the outcome; only after
        // the user confirms are the files deleted. Fully-decided selections use the plain
        // confirmation dialog as before.
        private async Task CleanupWithPreviewAsync(IList<DuplicateGroup> groups)
        {
            if (groups == null || groups.Count == 0) return;

            if (groups.Any(g => g.IsAmbiguous))
            {
                var win = new DuplicateDefaultResolveWindow(groups.ToList()) { Owner = this };
                if (win.ShowDialog() != true) return;
                await CleanupGroupsAsync(groups, skipConfirm: true);
            }
            else
            {
                await CleanupGroupsAsync(groups);
            }
        }

        private async void BtnDupResolveOne_Click(object sender, RoutedEventArgs e)
        {
            var g = (sender as FrameworkElement)?.DataContext as DuplicateGroup;
            if (g != null) await ResolveGroupAsync(g);
        }

        private async void MenuItemDupResolve_Click(object sender, RoutedEventArgs e)
        {
            if (GridDuplicates.SelectedItem is DuplicateGroup g)
                await ResolveGroupAsync(g);
        }

        private async Task ResolveGroupAsync(DuplicateGroup group)
        {
            var win = new DuplicateResolveWindow(group) { Owner = this };
            bool confirmed = win.ShowDialog() == true;
            if (confirmed) group.MarkDecided();   // user made an explicit choice
            UpdateDupCounts();
            if (confirmed)
                await CleanupGroupsAsync(new List<DuplicateGroup> { group }, skipConfirm: true);
        }

        // Deletes every copy currently marked DELETE in the given groups, logs each kept
        // and deleted file, and drops groups that no longer hold a duplicate. Groups with
        // no keeper (e.g. an unresolved same-folder group) are skipped so the last copy is
        // never deleted — those must be resolved via the modal first.
        private async Task CleanupGroupsAsync(IList<DuplicateGroup> groups, bool skipConfirm = false)
        {
            if (groups == null || groups.Count == 0) return;

            var actionable = groups.Where(g => g.Files.Any(f => f.Keep) &&
                                               g.Files.Any(f => !f.Keep)).ToList();
            int skippedNoKeeper = groups.Count(g => !g.Files.Any(f => f.Keep));
            if (skippedNoKeeper > 0)
                AppendLog($"Warning: {skippedNoKeeper} group(s) have no copy marked KEEP — " +
                          "use Resolve… to choose one first (skipped).");

            int totalDelete = actionable.Sum(g => g.Files.Count(f => !f.Keep));
            if (totalDelete == 0)
            {
                AppendLog("Warning: Nothing marked for deletion in the selected group(s).");
                return;
            }

            if (!skipConfirm)
            {
                var answer = MessageBox.Show(this,
                    $"Delete {totalDelete} duplicate file(s) from disk?\n\nThis cannot be undone.",
                    "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            foreach (var g in actionable)
            {
                var keepers = g.Files.Where(f => f.Keep).ToList();
                var doomed  = g.Files.Where(f => !f.Keep).ToList();
                if (doomed.Count == 0) continue;

                // Re-check the real filesystem right now — never trust scan-time or cached
                // state for a destructive action. A copy marked KEEP may have been moved or
                // deleted since the scan. If none of the keepers still exist we must NOT
                // delete the doomed ones, or the last remaining real file would be lost.
                var survivingKeepers = new List<DuplicateFile>();
                foreach (var k in keepers)
                    if (await Task.Run(() => File.Exists(k.FullPath)))
                        survivingKeepers.Add(k);

                if (survivingKeepers.Count == 0)
                {
                    AppendLog($"Warning: Skipped \"{g.FileName}\" — no copy marked KEEP still exists " +
                              "on disk; refusing to delete the last remaining file. Re-scan and resolve it again.");
                    continue;
                }

                foreach (var k in survivingKeepers)
                    AppendLog($"✔ Kept:    {k.FullPath}", _dupKeepColor);

                foreach (var d in doomed)
                {
                    string path = d.FullPath;
                    try
                    {
                        // Stat the live file (off the UI thread). -1 => gone.
                        long liveSize = await Task.Run(() =>
                        {
                            var fi = new FileInfo(path);
                            return fi.Exists ? fi.Length : -1L;
                        });

                        if (liveSize < 0)
                        {
                            // Already gone — nothing to delete, just drop it from view/cache.
                            _dupHashCache.Remove(path);
                            g.Files.Remove(d);
                            AppendLog($"Warning: Already gone (skipped): {path}");
                            continue;
                        }

                        if (liveSize != d.SizeBytes)
                        {
                            // Changed since it was hashed → no longer a confirmed duplicate. Keep it.
                            AppendLog($"Warning: Changed since scan — kept, not deleted: {path}");
                            continue;
                        }

                        await Task.Run(() => File.Delete(path));
                        _dupHashCache.Remove(path);
                        g.Files.Remove(d);
                        AppendLog($"🗑 Deleted: {path}", _dupDeleteColor);
                        await RemoveEmptyFoldersAsync(path);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Error: Delete failed: {path} — {ex.Message}");
                    }
                }

                g.NotifyCounts();
                if (g.Files.Count < 2) _dupGroups.Remove(g);  // no duplicate left to track
            }

            _dupHashCache.Save();
            if (_dupGroups.Count == 0)
            {
                TxtDupEmpty.Text       = "All duplicates cleaned up. 🎉";
                TxtDupEmpty.Visibility = Visibility.Visible;
            }
            UpdateDupCounts();
        }

        // After a file is deleted, remove its now-empty folder — and any parent folders
        // that became empty as a result — cascading up but never touching a configured
        // scan root (or anything above it). "Empty" means no files and no subfolders.
        private async Task RemoveEmptyFoldersAsync(string filePath)
        {
            var roots = _dupFolders.Select(f => f.Path.TrimEnd('\\', '/')).ToList();
            var removed = new List<string>();
            string error = null;

            await Task.Run(() =>
            {
                try
                {
                    string dir = Path.GetDirectoryName(filePath);
                    while (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        string norm = dir.TrimEnd('\\', '/');
                        if (roots.Any(r => string.Equals(norm, r, StringComparison.OrdinalIgnoreCase)))
                            break;                                            // don't delete a scan root
                        if (Directory.EnumerateFileSystemEntries(dir).Any())
                            break;                                            // still has content
                        string parent = Path.GetDirectoryName(dir);
                        Directory.Delete(dir);
                        removed.Add(dir);
                        dir = parent;
                    }
                }
                catch (Exception ex) { error = ex.Message; }
            });

            foreach (var d in removed)
                AppendLog($"🗑 Removed empty folder: {d}", _dupDeleteColor);
            if (error != null)
                AppendLog($"Warning: Could not remove empty folder: {error}");
        }

        // ── Duplicate Finder — ignore / open ──────────────────────────────────

        private void BtnDupIgnoreOne_Click(object sender, RoutedEventArgs e)
        {
            var g = (sender as FrameworkElement)?.DataContext as DuplicateGroup;
            if (g != null) IgnoreGroups(new List<DuplicateGroup> { g });
        }

        private void MenuItemDupIgnore_Click(object sender, RoutedEventArgs e)
            => IgnoreGroups(GetSelectedDupGroups());

        // Hides the group and remembers its hash so future scans skip it too.
        private void IgnoreGroups(IList<DuplicateGroup> groups)
        {
            if (groups == null || groups.Count == 0) return;
            foreach (var g in groups.ToList())
            {
                if (!string.IsNullOrEmpty(g.Hash)) _dupIgnored.Add(g.Hash);
                _dupGroups.Remove(g);
                AppendLog($"Ignored duplicate group: {g.FileName}");
            }
            SaveDuplicateIgnored();
            if (_dupGroups.Count == 0)
            {
                TxtDupEmpty.Text       = "No duplicate groups to show.";
                TxtDupEmpty.Visibility = Visibility.Visible;
            }
            UpdateDupCounts();
        }

        private void BtnDupPlayOne_Click(object sender, RoutedEventArgs e)
        {
            var f = (sender as FrameworkElement)?.DataContext as DuplicateFile;
            if (f == null) return;
            try
            {
                if (!File.Exists(f.FullPath))
                {
                    AppendLog($"Warning: File no longer exists: {f.FullPath}");
                    return;
                }
                MediaLauncher.Open(f.FullPath);
                AppendLog($"▶ Opened: {f.FullPath}");
            }
            catch (Exception ex)
            {
                AppendLog($"Error: Could not open file: {ex.Message}");
            }
        }

        private void SaveLibrarySettings()
            => SettingsStorage.Update(s =>
            {
                s.WatchedFolders = _watchedFolders.Select(f => f.Path).ToList();
                s.LibraryRules   = _libraryRules.ToList();
            });

        // ── Library Manager — folder management ──────────────────────────────

        private async void BtnLibAddFolder_Click(object sender, RoutedEventArgs e)
        {
            string picked = FolderPicker.Pick(title: "Select folder to watch for video files");
            if (picked != null && !_watchedFolders.Any(f => f.Path.Equals(picked, StringComparison.OrdinalIgnoreCase)))
            {
                _watchedFolders.Add(new WatchedFolderItem(picked));
                SaveLibrarySettings();
                await LibraryScanAsync();
            }
        }

        private void BtnLibRemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(LstWatchedFolders.SelectedItem is WatchedFolderItem selected)) return;

            _watchedFolders.Remove(selected);
            SaveLibrarySettings();

            // Remove scan results that came from the removed folder
            string prefix = selected.Path.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            var toRemove = _scanResults
                .Where(r => r.SourceFilePath != null &&
                            (r.SourceFilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                             r.SourceFilePath.Equals(selected.Path, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var r in toRemove) _scanResults.Remove(r);

            // Prune cache entries for that folder and persist
            _libraryCache.PruneFolder(selected.Path);
            _libraryCache.Save();
        }

        // ── Library Manager — rule management ────────────────────────────────

        private async void BtnLibAddRule_Click(object sender, RoutedEventArgs e)
        {
            var win = new LibraryRuleEditWindow { Owner = this };
            if (win.ShowDialog() == true)
            {
                _libraryRules.Add(win.Result);
                SaveLibrarySettings();
                await LibraryScanAsync();
                await LibraryApplyAsync();
            }
        }

        private async void BtnLibEditRule_Click(object sender, RoutedEventArgs e)
            => await EditSelectedRule();

        private async void GridRules_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => await EditSelectedRule();

        private async Task EditSelectedRule()
        {
            if (!(GridRules.SelectedItem is LibraryRule selected)) return;

            var win = new LibraryRuleEditWindow(selected) { Owner = this };
            if (win.ShowDialog() != true) return;

            int idx = _libraryRules.IndexOf(selected);
            if (idx >= 0)
            {
                _libraryRules[idx] = win.Result;
                SaveLibrarySettings();
                await LibraryScanAsync();
                await LibraryApplyAsync();
            }
        }

        private async void BtnLibRemoveRule_Click(object sender, RoutedEventArgs e)
        {
            if (!(GridRules.SelectedItem is LibraryRule selected)) return;
            _libraryRules.Remove(selected);
            SaveLibrarySettings();
            await LibraryScanAsync();
        }

        // ── Library Manager — scan ────────────────────────────────────────────

        private async void BtnLibScan_Click(object sender, RoutedEventArgs e)
            => await LibraryScanAsync();

        private async Task LibraryScanAsync()
        {
            BtnLibScan.IsEnabled       = false;
            ScanProgressRow.Visibility = Visibility.Collapsed;

            // Hide empty-state text; show loading overlay while the folder scan runs
            TxtGridEmpty.Visibility       = Visibility.Collapsed;
            TxtGridLoadingDetail.Text     = "Scanning folders…";
            GridLoadingPanel.Visibility   = Visibility.Visible;

            var folderItems = _watchedFolders.ToList();
            var rules       = _libraryRules.ToList();

            // Phase 1 — folder scan with async reachability pre-check.
            // Directory.Exists / GetFiles on an unreachable UNC path blocks at the Windows
            // kernel level (SMB redirector), stalling the whole process — not just the thread.
            // We avoid that by first doing a truly async TCP connect to port 445 (SMB).
            // TcpClient.ConnectAsync uses overlapped I/O and never blocks any thread.
            // IsAvailable on each WatchedFolderItem is updated here so the UI can show ⚠.
            var allFiles = new List<string>();
            foreach (var item in folderItems)
            {
                if (!await IsUncPathReachableAsync(item.Path))
                {
                    item.IsAvailable = false;
                    AppendLog($"⚠ Network folder not reachable (skipped): {item.Path}");
                    continue;
                }
                // For non-network paths check existence synchronously (fast, no SMB involved)
                if (!PathHelper.IsNetworkPath(item.Path) && !Directory.Exists(item.Path))
                {
                    item.IsAvailable = false;
                    AppendLog($"⚠ Folder not found (skipped): {item.Path}");
                    continue;
                }

                item.IsAvailable = true;
                var path     = item.Path;
                var scanTask = Task.Run(() => LibraryScanner.FindVideoFiles(path, recursive: true).ToList());
                if (await Task.WhenAny(scanTask, Task.Delay(TimeSpan.FromSeconds(30))) != scanTask)
                {
                    item.IsAvailable = false;
                    AppendLog($"⚠ Folder scan timed out: {item.Path}");
                    continue;
                }
                try   { allFiles.AddRange(await scanTask); }
                catch (Exception ex) { item.IsAvailable = false; AppendLog($"⚠ Error scanning {item.Path}: {ex.Message}"); }
            }

            if (allFiles.Count > 0)
            {
                var fileSet = new HashSet<string>(allFiles, StringComparer.OrdinalIgnoreCase);
                _libraryCache.PruneDeleted(fileSet);
            }

            // Build all ScanResult objects in one go, then swap the entire collection at once
            // to avoid triggering N individual UI updates (each Add fires CollectionChanged).
            // Fresh cache hits are pre-populated here so they show up instantly (flagged as
            // "cached"); uncached/stale files stay in the "Resolving..." state for Phase 2.
            //
            // Runs off the UI thread: cache pre-population stats each file and resolves its
            // target directory (filesystem / SMB I/O), which would otherwise freeze the UI
            // for large or network libraries. Safe because these ScanResults aren't bound
            // to the grid yet (no PropertyChanged listeners until the swap below).
            var initialResults = await Task.Run(() => allFiles.Select(fp =>
            {
                var rule = LibraryScanner.FindMatchingRule(fp, rules);
                var r = new ScanResult
                {
                    SourceFilePath  = fp,
                    Filename        = Path.GetFileName(fp),
                    Rule            = rule,
                    IsResolvingMeta = true,       // uncached items start as resolving
                    Status          = rule != null ? "Resolving..." : "No Rule"
                };

                CacheEntry cached = null;
                string     lastMod = null;
                try { lastMod = File.GetLastWriteTimeUtc(fp).ToString("O"); } catch { }
                bool fresh = lastMod != null && _libraryCache.TryGetFresh(fp, lastMod, out cached);

                if (fresh)
                {
                    ApplyCachedMeta(r, cached);   // show cached meta now, no spinner
                }
                else if (cached != null)
                {
                    // Stale meta (file changed) — keep the user's custom overrides only
                    r.CustomTargetDir          = cached.CustomTargetDir;
                    r.CustomTargetNameTemplate = cached.CustomTargetNameTemplate;
                }
                return r;
            }).ToList());

            _scanResults = new ObservableCollection<ScanResult>(initialResults);
            foreach (var r in initialResults)
                r.PropertyChanged += ScanResult_PropertyChanged;
            _scanViewSource.Source = _scanResults;
            UpdateScanResultsCount();

            // Phase 1 done — dismiss loading overlay; grid now shows rows with "···" indicators
            GridLoadingPanel.Visibility = Visibility.Collapsed;

            // Decide what still needs an API resolve. Uncached/stale items always do.
            // Fresh cache hits are only re-resolved when the user opted into it; otherwise
            // they stay as-is (and keep their "cached" flag) to save API calls.
            bool reResolveCached = (SettingsStorage.Load() ?? new AppSettings()).ReResolveLibraryOnScan;
            var  scanItems = initialResults
                             .Where(r => !r.IsFromCache || reResolveCached)
                             .ToList();
            int  total     = scanItems.Count;

            if (total == 0)
            {
                if (allFiles.Count == 0)
                    TxtGridEmpty.Visibility = Visibility.Visible;
                else
                    _libraryCache.Save();   // persist any pruning of deleted files
                BtnLibScan.IsEnabled = true;
                return;
            }

            ScanProgressBar.Maximum    = total;
            ScanProgressBar.Value      = 0;
            TxtScanEta.Text            = $"0 / {total}";
            ScanProgressRow.Visibility = Visibility.Visible;

            // Phase 2 — resolve meta with a 4-worker pool (max 4 concurrent API
            // requests). Each worker pulls the next pending item, preferring rows
            // that match the active search filter, so if the user filters mid-scan
            // the items they're actually looking at get resolved first.
            int  done      = 0;
            var  scanStart = DateTime.UtcNow;
            var  pending   = new LinkedList<ScanResult>(scanItems);

            ScanResult NextItem()
            {
                if (pending.Count == 0) return null;
                if (!string.IsNullOrEmpty(_searchText))
                {
                    for (var node = pending.First; node != null; node = node.Next)
                    {
                        if (MatchesSearch(node.Value))
                        {
                            pending.Remove(node);
                            return node.Value;
                        }
                    }
                }
                var first = pending.First.Value;
                pending.RemoveFirst();
                return first;
            }

            async Task Worker()
            {
                while (true)
                {
                    // Runs on the UI thread, so pulling from `pending` and updating
                    // progress need no locking.
                    var result = NextItem();
                    if (result == null) return;

                    try   { await ResolveMetaAsync(result); }
                    finally
                    {
                        done++;
                        ScanProgressBar.Value = done;
                        var    elapsed = DateTime.UtcNow - scanStart;
                        double rate    = done / Math.Max(elapsed.TotalSeconds, 0.001);
                        int    left    = total - done;
                        TxtScanEta.Text = left > 0 && rate > 0
                            ? $"{done} / {total}  –  {FormatEta(TimeSpan.FromSeconds(left / rate))} left"
                            : $"{done} / {total}";
                    }
                }
            }

            var workers = Enumerable.Range(0, 4).Select(_ => Worker()).ToList();
            await Task.WhenAll(workers);

            _libraryCache.Save();
            UpdateScanResultsCount();
            TxtScanEta.Text = $"{total} / {total}  –  Done";
            await Task.Delay(1200);
            ScanProgressRow.Visibility = Visibility.Collapsed;
            BtnLibScan.IsEnabled = true;
        }

        // Populates a freshly-built ScanResult from a fresh cache entry so it can be
        // shown immediately, without waiting for (or instead of) an API re-resolve.
        private void ApplyCachedMeta(ScanResult result, CacheEntry cached)
        {
            result.FranchiseName            = cached.FranchiseName;
            result.SeasonName               = cached.SeasonName;
            result.SeasonNumber             = cached.SeasonNumber;
            result.FranchiseSource          = ParseMetaSource(cached.FranchiseSource);
            result.SeasonSource             = ParseMetaSource(cached.SeasonSource);
            result.EpisodeNumber            = LibraryScanner.ExtractEpisodeNumber(result.Filename);
            result.EpisodeType              = LibraryScanner.ExtractType(result.Filename);
            result.CustomTargetDir          = cached.CustomTargetDir;
            result.CustomTargetNameTemplate = cached.CustomTargetNameTemplate;

            if (result.Rule != null)
            {
                try
                {
                    var resolution = LibraryScanner.ResolveTargetDirectory(
                        result.Rule, cached.FranchiseName, cached.SeasonName, cached.SeasonNumber);
                    result.TargetDir      = resolution.TargetDir;
                    result.TargetFilename = result.Rule.UseCleanFilename
                        ? LibraryScanner.CleanFilename(result.Filename)
                        : result.Filename;
                    result.Status = "Preview";
                }
                catch { /* leave as "Resolving..."; Phase 2 will retry if enabled */ }
            }

            result.IsFromCache     = true;
            result.IsResolvingMeta = false;
        }

        private static MetaSource ParseMetaSource(string s)
        {
            if (s == "Api")   return MetaSource.Api;
            if (s == "Local") return MetaSource.Local;
            return MetaSource.Unknown;
        }

        // Resolves franchise/season for one grid item.
        // Always runs for every item — API first, local extraction as fallback.
        // Items with no matching rule still get franchise/season shown in the grid.
        private async Task ResolveMetaAsync(ScanResult result)
        {
            try
            {
                string franchise       = null;
                string seasonName      = null;
                int    seasonNum       = -1;
                bool   franchiseFromApi = false;
                bool   seasonFromApi   = false;

                // ── Restore custom overrides from cache (always, regardless of freshness) ──
                string lastMod = null;
                try { lastMod = File.GetLastWriteTimeUtc(result.SourceFilePath).ToString("O"); } catch { }
                _libraryCache.TryGetFresh(result.SourceFilePath, lastMod, out CacheEntry cached);
                if (cached != null)
                {
                    result.CustomTargetDir          = cached.CustomTargetDir;
                    result.CustomTargetNameTemplate = cached.CustomTargetNameTemplate;
                }

                // ── API: franchise name ───────────────────────────────────────────────────
                // Try even when CreateFranchiseFolder is off: the column is purely a preview.
                try
                {
                    string r = await Task.Run(() => _api.GetFranchiseName(result.Filename));
                    if (!string.IsNullOrWhiteSpace(r)) { franchise = r.Trim(); franchiseFromApi = true; }
                }
                catch { }

                // ── API: season name + number ─────────────────────────────────────────────
                try
                {
                    string r = await Task.Run(() => _api.GetSeasonName(result.Filename));
                    if (!string.IsNullOrWhiteSpace(r)) { seasonName = r.Trim(); seasonFromApi = true; }
                }
                catch { }
                if (seasonName != null)
                {
                    try { seasonNum = await Task.Run(() => _api.GetSeasonNumber(result.Filename)); } catch { }
                }

                // ── Local fallback: franchise ─────────────────────────────────────────────
                if (string.IsNullOrWhiteSpace(franchise))
                    franchise = LibraryScanner.ExtractBaseName(result.Filename);

                // ── Local fallback: season ────────────────────────────────────────────────
                if (string.IsNullOrWhiteSpace(seasonName))
                {
                    var (sName, sNum) = LibraryScanner.ExtractSeasonInfo(result.Filename);
                    if (!string.IsNullOrWhiteSpace(sName))
                    {
                        seasonName = sName;
                        seasonNum  = sNum;
                        // leave seasonFromApi = false so it gets amber coloring
                    }
                }

                // ── Write back to cache ───────────────────────────────────────────────────
                if (lastMod != null)
                {
                    _libraryCache.Set(result.SourceFilePath, new CacheEntry
                    {
                        LastModifiedUtc          = lastMod,
                        FranchiseName            = franchise,
                        SeasonName               = seasonName,
                        SeasonNumber             = seasonNum,
                        FranchiseSource          = franchiseFromApi ? "Api" : "Local",
                        SeasonSource             = seasonFromApi    ? "Api" : "Local",
                        CustomTargetDir          = result.CustomTargetDir,
                        CustomTargetNameTemplate = result.CustomTargetNameTemplate
                    });
                }

                // ── Update ScanResult properties (fires INotifyPropertyChanged → UI updates) ─
                result.FranchiseName   = franchise;
                result.SeasonName      = seasonName;
                result.SeasonNumber    = seasonNum;
                result.FranchiseSource = franchiseFromApi ? MetaSource.Api : MetaSource.Local;
                result.SeasonSource    = seasonFromApi    ? MetaSource.Api
                                       : (!string.IsNullOrEmpty(seasonName) ? MetaSource.Local : MetaSource.Unknown);
                result.EpisodeNumber   = LibraryScanner.ExtractEpisodeNumber(result.Filename);
                result.EpisodeType     = LibraryScanner.ExtractType(result.Filename);

                // ── Compute target path (only when a rule is matched) ─────────────────────
                if (result.Rule != null)
                {
                    var resolution = await Task.Run(() =>
                        LibraryScanner.ResolveTargetDirectory(result.Rule, franchise, seasonName, seasonNum));
                    result.TargetDir      = resolution.TargetDir;
                    result.TargetFilename = result.Rule.UseCleanFilename
                        ? LibraryScanner.CleanFilename(result.Filename)
                        : result.Filename;
                    result.Status = "Preview";
                }
                // "No Rule" items keep their status and have no target path — that's correct.

                // Freshly resolved this session — drop the "cached" flag.
                result.IsFromCache = false;
            }
            catch (Exception ex)
            {
                result.Status = $"Error: {ex.Message}";
            }
            finally
            {
                // Always clear the loading indicator, even if an exception occurred
                result.IsResolvingMeta = false;
            }
        }

        private void UpdateScanResultsCount()
        {
            UpdateMoveAllButton();
            UpdateMoveQueueCount();
            UpdateGridEmptyState();

            if (TxtScanResultsCount == null) return;
            int total = _scanResults?.Count ?? 0;
            if (_scanViewSource?.View == null || string.IsNullOrEmpty(_searchText))
            {
                TxtScanResultsCount.Text = total == 0 ? "" : $"{total}";
                return;
            }
            int visible = _scanViewSource.View.OfType<ScanResult>().Count();
            TxtScanResultsCount.Text = visible == total
                ? $"{total}"
                : $"{visible} of {total}";
        }

        // The Move-All button only ever moves the rows currently shown by the
        // filter, so relabel it when a filter narrows the list down.
        private void UpdateMoveAllButton()
        {
            if (BtnMoveAll == null) return;

            int total   = _scanResults?.Count ?? 0;
            int visible = _scanViewSource?.View?.OfType<ScanResult>().Count() ?? total;

            bool filtered = !string.IsNullOrEmpty(_searchText) && visible < total;
            if (filtered)
            {
                BtnMoveAll.Content = "Move Shown →";
                BtnMoveAll.ToolTip = "Move only the files currently shown by the filter";
            }
            else
            {
                BtnMoveAll.Content = "Move All →";
                BtnMoveAll.ToolTip = "Move every previewed file";
            }
        }

        // Fires 3 s after the filter string stops changing.
        // Retries API for every visible item that only has local-fallback franchise/season data.
        private async void FilterRetryTimer_Tick(object sender, EventArgs e)
        {
            _filterRetryTimer.Stop();

            if (_scanViewSource?.View == null) return;

            // Only visible items whose franchise came from local extraction, not the API
            var candidates = _scanViewSource.View
                .OfType<ScanResult>()
                .Where(r => !r.IsResolvingMeta &&
                            (r.FranchiseSource == MetaSource.Local ||
                             r.SeasonSource    == MetaSource.Local))
                .ToList();

            if (candidates.Count == 0) return;

            // Mark all as resolving so the "···" indicator appears immediately
            foreach (var r in candidates)
                r.IsResolvingMeta = true;

            var sem = new SemaphoreSlim(4, 4);
            var tasks = candidates.Select(async result =>
            {
                await sem.WaitAsync();
                try   { await ResolveMetaAsync(result); }
                finally { sem.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
            _libraryCache.Save();
        }

        // ── Library Manager — apply ───────────────────────────────────────────

        private async Task LibraryApplyAsync()
        {
            var toMove = _scanResults.Where(r => r.Status == "Preview").ToList();
            await MoveBatchAsync(toMove);
        }

        // ── Library Manager — search ─────────────────────────────────────────

        private void TxtLibSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = TxtLibSearch.Text ?? "";
            _scanViewSource?.View?.Refresh();
            UpdateScanResultsCount();

            // Reset debounce timer — fires 3 s after the user stops typing
            _filterRetryTimer?.Stop();
            if (!string.IsNullOrEmpty(_searchText))
                _filterRetryTimer?.Start();
        }

        private void ScanResultsView_Filter(object sender, FilterEventArgs e)
        {
            var r = e.Item as ScanResult;
            bool ok = MatchesSearch(r);
            // Move Queue tab only lists items the user has told to move whose move
            // hasn't finished yet.
            if (ok && _moveQueueTab)
                ok = r != null && r.IsQueuedForMove;
            e.Accepted = ok;
        }

        // ── Library Manager — sub-tabs (Scan Results / Move Queue) ───────────

        private void BtnTabScanResults_Click(object sender, RoutedEventArgs e)
            => SetLibraryTab(moveQueue: false);

        private void BtnTabMoveQueue_Click(object sender, RoutedEventArgs e)
            => SetLibraryTab(moveQueue: true);

        private void SetLibraryTab(bool moveQueue)
        {
            _moveQueueTab = moveQueue;

            // Active-tab highlight (Tag="active" drives the SubTab style).
            if (BtnTabScanResults != null) BtnTabScanResults.Tag = moveQueue ? null : "active";
            if (BtnTabMoveQueue   != null) BtnTabMoveQueue.Tag   = moveQueue ? "active" : null;

            _scanViewSource?.View?.Refresh();   // re-applies ScanResultsView_Filter
            UpdateScanResultsCount();
            UpdateGridEmptyState();
        }

        // Updates the Move Queue tab label with the count of items still pending a move.
        private void UpdateMoveQueueCount()
        {
            if (BtnTabMoveQueue == null) return;
            int pending = _scanResults?.Count(r => r.IsQueuedForMove) ?? 0;
            string label = LocalizationManager.Get("LibTabMoveQueue");
            BtnTabMoveQueue.Content = pending > 0 ? $"{label} ({pending})" : label;
        }

        // Shows the "Move Queue is empty" hint when the Move Queue tab is active but the
        // filtered grid has no rows (yet there are scan results behind it).
        private void UpdateGridEmptyState()
        {
            if (TxtMoveQueueEmpty == null) return;
            bool hasResults = (_scanResults?.Count ?? 0) > 0;
            bool queueEmpty = _scanViewSource?.View?.OfType<ScanResult>().Any() != true;
            TxtMoveQueueEmpty.Visibility =
                (_moveQueueTab && hasResults && queueEmpty) ? Visibility.Visible : Visibility.Collapsed;
        }

        // A row's status changed (resolved → Preview, moved → Moved, …). Keep the
        // Move Queue tab count live, and drop/add rows on the Move Queue tab as they
        // enter or leave the "pending move" state.
        private void ScanResult_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ScanResult.IsQueuedForMove)) return;
            UpdateMoveQueueCount();
            if (_moveQueueTab)
            {
                _scanViewSource?.View?.Refresh();
                UpdateGridEmptyState();
            }
        }

        // True when the row matches the active search filter (or no filter is set).
        // Shared by the view filter and the scan resolution queue so both stay in sync.
        private bool MatchesSearch(ScanResult r)
        {
            if (string.IsNullOrEmpty(_searchText)) return true;
            return r != null &&
                (r.Filename?.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase)     >= 0 ||
                 r.FranchiseName?.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ── Library Manager — move ────────────────────────────────────────────

        private async void BtnMoveAll_Click(object sender, RoutedEventArgs e)
        {
            var visible = _scanViewSource?.View?.OfType<ScanResult>()
                          .Where(r => r.Status == "Preview")
                          .ToList();
            await MoveBatchAsync(visible);
        }

        private async void BtnMoveOne_Click(object sender, RoutedEventArgs e)
        {
            var result = (sender as FrameworkElement)?.DataContext as ScanResult;
            if (result != null)
                await MoveBatchAsync(new List<ScanResult> { result });
        }

        // ── Library Manager — delete source file(s) ──────────────────────────

        private async void BtnDeleteOne_Click(object sender, RoutedEventArgs e)
        {
            var result = (sender as FrameworkElement)?.DataContext as ScanResult;
            if (result != null)
                await DeleteFilesAsync(new List<ScanResult> { result });
        }

        private async void MenuItemDeleteSelected_Click(object sender, RoutedEventArgs e)
            => await DeleteFilesAsync(GetTargetGridSelection());

        // Permanently deletes the underlying file(s) from disk after an explicit
        // confirmation, then drops the rows from the grid and the cache.
        private async Task DeleteFilesAsync(IList<ScanResult> items)
        {
            if (items == null || items.Count == 0) return;

            string prompt = items.Count == 1
                ? $"Delete this file from disk?\n\n{items[0].SourceFilePath}"
                : $"Delete these {items.Count} files from disk?";
            var answer = MessageBox.Show(this,
                prompt + "\n\nThis cannot be undone.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            foreach (var r in items)
            {
                try
                {
                    string path = r.SourceFilePath;
                    await Task.Run(() => { if (File.Exists(path)) File.Delete(path); });
                    _libraryCache.Remove(path);
                    r.PropertyChanged -= ScanResult_PropertyChanged;
                    _scanResults.Remove(r);
                    AppendLog($"🗑 Deleted: {path}");
                }
                catch (Exception ex)
                {
                    r.Status = $"Error: {ex.Message}";
                    AppendLog($"⚠ Delete failed: {r.SourceFilePath} — {ex.Message}");
                }
            }

            _libraryCache.Save();
            UpdateScanResultsCount();
        }

        // Moves a batch of items. All items are flagged into the Move Queue up front
        // (so the queued-but-not-yet-started ones are visible there), then processed in
        // order; each leaves the queue the moment its move finishes (success/skip/error).
        private async Task MoveBatchAsync(IList<ScanResult> items)
        {
            if (items == null || items.Count == 0) return;

            foreach (var r in items)
                if (r.Status == "Preview")
                    r.IsQueuedForMove = true;

            foreach (var r in items)
            {
                if (!r.IsQueuedForMove) continue;
                try     { await MoveFileAsync(r); }
                finally { r.IsQueuedForMove = false; }
            }
        }

        private async Task MoveFileAsync(ScanResult result)
        {
            if (result.IsMoving || result.Status == "Moved") return;

            result.IsMoving = true;
            result.Status   = "Moving...";

            try
            {
                string destination;
                TargetResolution resolution = null;

                // For smart-season rules, re-resolve at move time so the filesystem state
                // is current even if other files were moved since the last scan.
                if (!result.HasCustomDir
                    && result.Rule?.CreateFranchiseFolder == true
                    && result.Rule?.CreateSeasonFolderIfMultiple == true
                    && !string.IsNullOrEmpty(result.FranchiseName))
                {
                    resolution = await Task.Run(() =>
                        LibraryScanner.ResolveTargetDirectory(
                            result.Rule, result.FranchiseName, result.SeasonName, result.SeasonNumber));
                    destination = Path.Combine(resolution.TargetDir, result.EffectiveTargetName);
                }
                else
                {
                    destination = result.EffectiveTargetPath;
                }

                if (string.IsNullOrEmpty(destination))
                {
                    result.Status = "Error: No target path";
                    return;
                }

                // Migrate flat files to their own season subfolder before placing the new season
                if (resolution?.NeedsMigration == true)
                    await Task.Run(() => ExecuteMigration(resolution));

                // Handle conflict (check on background thread, resolve on UI thread)
                bool conflict = await Task.Run(() => File.Exists(destination));
                if (conflict)
                {
                    string action = _conflictBehavior;
                    if (action == "ask")
                        action = ShowConflictDialog(Path.GetFileName(destination));

                    switch (action)
                    {
                        case "skip":
                            result.Status   = "Skipped";
                            result.IsMoving = false;
                            return;
                        case "rename":
                            destination = await Task.Run(() => FindUniquePath(destination));
                            break;
                        // "overwrite": fall through — File.Move with pre-delete below
                    }
                }

                string finalDest = destination;
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(finalDest));
                    if (File.Exists(finalDest))
                        File.Delete(finalDest); // overwrite
                    File.Move(result.SourceFilePath, finalDest);
                });
                destination = finalDest;

                // Update _kizuna.info when file lands flat in the franchise folder
                if (resolution?.IsFlat == true && resolution.FranchiseDir != null)
                {
                    string movedName = Path.GetFileName(destination);
                    string season    = result.SeasonName;
                    int    seasonNum = result.SeasonNumber;
                    await Task.Run(() =>
                    {
                        var info = FranchiseInfo.ExistsIn(resolution.FranchiseDir)
                            ? FranchiseInfo.Read(resolution.FranchiseDir)
                            : new FranchiseInfo { SeasonName = season, SeasonNumber = seasonNum };
                        if (string.IsNullOrEmpty(info.SeasonName))
                        { info.SeasonName = season; info.SeasonNumber = seasonNum; }
                        if (!info.Files.Contains(movedName, StringComparer.OrdinalIgnoreCase))
                            info.Files.Add(movedName);
                        info.Write(resolution.FranchiseDir);
                    });
                }

                result.Status = "Moved";

                // Record for undo (keep source path before it was changed by move)
                _moveHistory.Push((result.SourceFilePath, destination, result));
                BtnUndo.IsEnabled = true;
            }
            catch (Exception ex)
            {
                result.Status = $"Error: {ex.Message}";
            }
            finally
            {
                result.IsMoving = false;
            }
        }

        private static void ExecuteMigration(TargetResolution resolution)
        {
            var    info      = resolution.ExistingInfo;
            string oldName   = info.SeasonNumber > 0
                ? $"{info.SeasonName} (S{info.SeasonNumber:D2})"
                : info.SeasonName;
            string oldDir    = Path.Combine(resolution.FranchiseDir,
                                            LibraryScanner.SanitizeFolderName(oldName));
            Directory.CreateDirectory(oldDir);

            foreach (var filename in info.Files)
            {
                string src = Path.Combine(resolution.FranchiseDir, filename);
                string dst = Path.Combine(oldDir, filename);
                try { if (File.Exists(src) && !File.Exists(dst)) File.Move(src, dst); }
                catch { }
            }

            // Remove _kizuna.info when no flat video files remain in the franchise folder
            bool flatVideosRemain = Directory.GetFiles(resolution.FranchiseDir)
                .Any(f => !string.Equals(Path.GetFileName(f), FranchiseInfo.FileName,
                                         StringComparison.OrdinalIgnoreCase)
                       && LibraryScanner.IsVideoFile(f));
            if (!flatVideosRemain)
            {
                string infoPath = FranchiseInfo.GetPath(resolution.FranchiseDir);
                try { if (File.Exists(infoPath)) File.Delete(infoPath); } catch { }
            }
        }

        // ── Library Manager — context menu (target grid) ─────────────────────

        private List<ScanResult> GetTargetGridSelection()
        {
            return GridFiles.SelectedItems.OfType<ScanResult>().ToList();
        }

        private async void MenuItemMoveSelected_Click(object sender, RoutedEventArgs e)
        {
            // Only rows with a resolved target are movable (skip "No Rule", already moved, etc.)
            var selected = GetTargetGridSelection()
                           .Where(r => r.Status == "Preview")
                           .ToList();
            await MoveBatchAsync(selected);
        }

        private void MenuItemChangeTargetDir_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetTargetGridSelection();
            if (selected.Count == 0) return;

            string initialDir = selected[0].EffectiveTargetDir;
            string picked = FolderPicker.Pick(initialPath: initialDir, title: "Select destination folder");
            if (picked == null) return;
            foreach (var r in selected)
            {
                r.CustomTargetDir = picked;
                _libraryCache.SetCustomTarget(r.SourceFilePath, r.CustomTargetDir, r.CustomTargetNameTemplate);
            }
            _libraryCache.Save();
        }

        private void MenuItemChangeTargetName_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetTargetGridSelection();
            if (selected.Count == 0) return;

            // Use the first selected row to seed the dialog
            var first = selected[0];
            var dlg = new NameTemplateDialog(
                first.CustomTargetNameTemplate ?? first.TargetFilename,
                first.EpisodeNumber,
                first.EpisodeType) { Owner = this };

            if (dlg.ShowDialog() != true) return;

            foreach (var r in selected)
            {
                r.CustomTargetNameTemplate = dlg.ResultTemplate;
                _libraryCache.SetCustomTarget(r.SourceFilePath, r.CustomTargetDir, r.CustomTargetNameTemplate);
            }
            _libraryCache.Save();
        }

        private void MenuItemRemoveCustomDir_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in GetTargetGridSelection())
            {
                r.CustomTargetDir = null;
                _libraryCache.SetCustomTarget(r.SourceFilePath, null, r.CustomTargetNameTemplate);
            }
            _libraryCache.Save();
        }

        private void MenuItemRemoveCustomName_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in GetTargetGridSelection())
            {
                r.CustomTargetNameTemplate = null;
                _libraryCache.SetCustomTarget(r.SourceFilePath, r.CustomTargetDir, null);
            }
            _libraryCache.Save();
        }

        // ── Tray indicators ───────────────────────────────────────────────────

        private void UpdateTrayIndicators()
        {
            bool hasDl     = _recognizedFiles?.Any(f => !f.IsInTransmission) == true;
            bool hasUnrec  = _unrecognizedFiles?.Count > 0;
            _tray?.UpdateIndicators(hasDl, hasUnrec);
        }

        // ── Grid search ───────────────────────────────────────────────────────

        private void TxtSearchUnrecognized_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchUnrecognized = TxtSearchUnrecognized.Text ?? "";
            CollectionViewSource.GetDefaultView(_unrecognizedFiles)?.Refresh();
        }

        private void TxtSearchDownload_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDownload = TxtSearchDownload.Text ?? "";
            CollectionViewSource.GetDefaultView(_recognizedFiles)?.Refresh();
        }

        // ── Export log ────────────────────────────────────────────────────────

        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title            = "Export Event Log",
                Filter           = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName         = $"kizuna-log-{DateTime.Now:yyyy-MM-dd}.txt",
                DefaultExt       = ".txt",
                AddExtension     = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                using (var sw = new StreamWriter(dlg.FileName, append: false, encoding: System.Text.Encoding.UTF8))
                {
                    foreach (var block in TxtEventLog.Document.Blocks.OfType<Paragraph>())
                    {
                        string line = string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text));
                        sw.WriteLine(line);
                    }
                }
                AppendLog($"Log exported to {dlg.FileName}");
            }
            catch (Exception ex)
            {
                AppendLog($"Error: Could not export log — {ex.Message}");
            }
        }

        // ── Library Manager — undo ────────────────────────────────────────────

        private async void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_moveHistory.Count == 0) return;
            var (from, to, row) = _moveHistory.Pop();

            try
            {
                await Task.Run(() =>
                {
                    if (!File.Exists(to))
                        throw new FileNotFoundException($"File not found: {Path.GetFileName(to)}");
                    Directory.CreateDirectory(Path.GetDirectoryName(from));
                    File.Move(to, from);
                });

                row.Status = "Preview";
                AppendLog($"Undone: '{Path.GetFileName(to)}' moved back.");
            }
            catch (Exception ex)
            {
                AppendLog($"Error: Undo failed — {ex.Message}");
                _moveHistory.Push((from, to, row)); // push back so user can retry
            }

            BtnUndo.IsEnabled = _moveHistory.Count > 0;
        }

        // ── Library Manager — rule test ───────────────────────────────────────

        private void BtnLibTestRule_Click(object sender, RoutedEventArgs e)
        {
            // Toggle the inline test panel
            if (RuleTestRow.Height == new GridLength(0))
            {
                RuleTestRow.Height = GridLength.Auto;
                TxtRuleTestInput.Focus();
            }
            else
            {
                RuleTestRow.Height = new GridLength(0);
                TxtRuleTestResult.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnCloseRuleTest_Click(object sender, RoutedEventArgs e)
        {
            RuleTestRow.Height = new GridLength(0);
            TxtRuleTestResult.Visibility = Visibility.Collapsed;
        }

        private void TxtRuleTestInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) RunRuleTest();
        }

        private void BtnRunRuleTest_Click(object sender, RoutedEventArgs e)
            => RunRuleTest();

        private void RunRuleTest()
        {
            string filename = TxtRuleTestInput.Text?.Trim();
            if (string.IsNullOrEmpty(filename))
            {
                TxtRuleTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                TxtRuleTestResult.Text       = "Enter a filename above and press ▶ Test.";
                TxtRuleTestResult.Visibility = Visibility.Visible;
                return;
            }

            var rules = _libraryRules?.ToList() ?? new List<LibraryRule>();
            var rule  = LibraryScanner.FindMatchingRule(filename, rules);

            if (rule == null)
            {
                TxtRuleTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xC4, 0x1F));
                TxtRuleTestResult.Text       = "⚠  No rule matched.";
            }
            else
            {
                string cleanName = rule.UseCleanFilename
                    ? LibraryScanner.CleanFilename(filename)
                    : filename;
                string franchise = LibraryScanner.ExtractBaseName(filename);
                var (sName, sNum) = LibraryScanner.ExtractSeasonInfo(filename);
                var resolution   = LibraryScanner.ResolveTargetDirectory(rule, franchise, sName, sNum);
                string target    = Path.Combine(resolution.TargetDir, cleanName);

                TxtRuleTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0x78, 0xC8, 0x78));
                TxtRuleTestResult.Text       = $"✓  Rule: {rule.Name}\n→  {target}";
            }

            TxtRuleTestResult.Visibility = Visibility.Visible;
        }

        // ── Conflict dialog (code-only WPF window) ────────────────────────────

        private string ShowConflictDialog(string filename)
        {
            string choice = "skip";

            var win = new Window
            {
                Title                  = "File Already Exists",
                Width                  = 420, Height = 170,
                WindowStartupLocation  = WindowStartupLocation.CenterOwner,
                Owner                  = this,
                ResizeMode             = ResizeMode.NoResize,
                WindowStyle            = WindowStyle.ToolWindow,
                Background             = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26))
            };

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text         = $"'{filename}' already exists at the target location.",
                Foreground   = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                TextWrapping = TextWrapping.Wrap,
                FontSize     = 12,
                Margin       = new Thickness(0, 0, 0, 16)
            });

            var btns = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            void AddBtn(string label, string tag)
            {
                var btn = new Button
                {
                    Content         = label,
                    Width           = 100, Height = 32,
                    Margin          = new Thickness(0, 0, 8, 0),
                    Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                    Foreground      = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)),
                    BorderThickness = new Thickness(1)
                };
                btn.Click += (s, _) => { choice = tag; win.Close(); };
                btns.Children.Add(btn);
            }

            AddBtn("Skip", "skip");
            AddBtn("Rename", "rename");
            AddBtn("Overwrite", "overwrite");
            root.Children.Add(btns);
            win.Content = root;
            win.ShowDialog();
            return choice;
        }

        // ── Conflict rename helper ────────────────────────────────────────────

        private static string FindUniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir      = Path.GetDirectoryName(path);
            string nameNoEx = Path.GetFileNameWithoutExtension(path);
            string ext      = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, $"{nameNoEx} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException($"Cannot find a unique filename for '{Path.GetFileName(path)}'.");
        }

        // ── Download All dropdown ─────────────────────────────────────────────

        private void BtnDownloadAllDropdown_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = BtnDownloadAllDropdown };
            foreach (int ms in _delayMs)
            {
                var label = FormatDelayLabel(ms);
                var item  = new MenuItem { Header = label, IsChecked = ms == _batchDelayMs };
                int captured = ms;
                item.Click += (s, _) =>
                {
                    _batchDelayMs = captured;
                    TxtDelayIndicator.Text = FormatDelayLabel(_batchDelayMs);
                    SettingsStorage.Update(st => st.BatchDelayMs = _batchDelayMs);
                    // Sync hidden ComboBox so existing CboDelay logic stays consistent
                    _populatingDelay = true;
                    for (int i = 0; i < _delayMs.Length; i++)
                        if (_delayMs[i] == _batchDelayMs) { CboDelay.SelectedIndex = i; break; }
                    _populatingDelay = false;
                };
                menu.Items.Add(item);
            }
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen    = true;
        }
    }
}

