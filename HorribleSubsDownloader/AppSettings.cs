using System.Collections.Generic;

namespace Kizuna
{
    /// <summary>
    /// All user-configurable and persisted application state.
    /// Serialized to JSON and stored encrypted on disk via <see cref="SettingsStorage"/>.
    /// </summary>
    public class AppSettings
    {
        public string ApiBaseUrl          { get; set; }
        public string Username            { get; set; }
        public string ApiToken            { get; set; }
        public string TorrentDownloadPath { get; set; }

        /// <summary>UI language key: "English", "German", "French", "Spanish", "Japanese".</summary>
        public string Language { get; set; } = "English";

        /// <summary>
        /// True when at least one layout save has occurred. Prevents restoring zeroed
        /// values from a settings file that predates the layout-persistence feature.
        /// </summary>
        public bool HasSavedLayout { get; set; }

        public double WindowLeft    { get; set; }
        public double WindowTop     { get; set; }
        public double WindowWidth   { get; set; }
        public double WindowHeight  { get; set; }

        /// <summary>Star-unit width for the left (unrecognized files) panel column.</summary>
        public double LeftPanelStars  { get; set; } = 1;

        /// <summary>Star-unit width for the right (download queue) panel column.</summary>
        public double RightPanelStars { get; set; } = 2;

        /// <summary>Whether the event-log strip is expanded.</summary>
        public bool LogExpanded { get; set; } = true;

        /// <summary>Milliseconds to wait between items during Download All (0 = immediate).</summary>
        public int BatchDelayMs { get; set; } = 5000;

        /// <summary>"Normal" or "Maximized".</summary>
        public string WindowState { get; set; } = "Normal";

        /// <summary>Property path last sorted in the download grid (e.g. "Filename").</summary>
        public string SortColumnPath { get; set; }

        /// <summary>Sort direction for the persisted column.</summary>
        public bool SortAscending { get; set; } = true;

        /// <summary>Folders the Library Manager scans for video files.</summary>
        public List<string> WatchedFolders { get; set; } = new List<string>();

        /// <summary>File-organisation rules defined in the Library Manager.</summary>
        public List<LibraryRule> LibraryRules { get; set; } = new List<LibraryRule>();

        /// <summary>Folders the Duplicate Finder scans, in priority order. The folder
        /// highest in the list keeps its copy by default; lower ones are deleted.</summary>
        public List<string> DuplicateFolders { get; set; } = new List<string>();

        /// <summary>Content hashes of duplicate groups the user chose to ignore. Ignored
        /// groups are hidden from the Duplicate Finder and skipped on future scans.</summary>
        public List<string> DuplicateIgnoredHashes { get; set; } = new List<string>();

        // ── Behavior ──────────────────────────────────────────────────────────

        /// <summary>When true, closing/minimizing the window hides it to the system tray.</summary>
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>When true, the app is registered in the Windows startup registry.</summary>
        public bool StartWithWindows { get; set; } = false;

        /// <summary>When true, tray balloon notifications are shown (download started/complete/error).</summary>
        public bool NotificationsEnabled { get; set; } = false;

        /// <summary>Automatically retry failed torrent adds (up to 3 times).</summary>
        public bool AutoRetryEnabled { get; set; } = true;

        /// <summary>Milliseconds to wait before retrying a failed add.</summary>
        public int AutoRetryDelayMs { get; set; } = 10000;

        /// <summary>What to do when the Library Manager target file already exists.
        /// Values: "ask", "skip", "overwrite", "rename".</summary>
        public string OnFileConflict { get; set; } = "ask";

        /// <summary>When true, cached Library-Manager scan results are re-resolved against
        /// the API on each scan. When false, fresh cache hits are shown as-is and only
        /// uncached files are resolved. Cached rows are flagged in the grid either way.</summary>
        public bool ReResolveLibraryOnScan { get; set; } = true;

        // ── Transmission ──────────────────────────────────────────────────────

        /// <summary>Full RPC URL of the Transmission daemon.</summary>
        public string TransmissionUrl { get; set; } = "http://127.0.0.1:9091/transmission/rpc";
    }
}
