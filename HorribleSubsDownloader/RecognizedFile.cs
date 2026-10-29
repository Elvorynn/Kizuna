using System.ComponentModel;

namespace Kizuna
{
    /// <summary>
    /// A file the backend has matched to a download rule and queued for download.
    /// Implements <see cref="INotifyPropertyChanged"/> so that UI bindings react to
    /// per-row download state changes without a full grid refresh.
    /// </summary>
    public class RecognizedFile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // ── API-bound properties (must match JSON keys) ───────────────────────

        public string Id         { get; set; }
        public string Filename   { get; set; }
        public string PubDate    { get; set; }
        public string FolderShort { get; set; }
        public string Path       { get; set; }
        public string Magnet     { get; set; }
        public string MagnetHash { get; set; }

        // ── UI state ──────────────────────────────────────────────────────────

        private bool _isDownloading;
        private bool _isAdded;
        private bool _isLocked;
        private bool _isInTransmission;
        private double _progress;
        private long _downloadSpeedBytes;
        private int _downloadEta = -1;

        /// <summary>True while this specific item is being handed off to Transmission.</summary>
        public bool IsDownloading
        {
            get => _isDownloading;
            set { _isDownloading = value; Notify(nameof(IsDownloading)); }
        }

        /// <summary>
        /// Set to true immediately after a successful add; triggers the success-flash
        /// row style before the item is removed from the collection.
        /// </summary>
        public bool IsAdded
        {
            get => _isAdded;
            set { _isAdded = value; Notify(nameof(IsAdded)); }
        }

        /// <summary>
        /// True when a batch Download All is in progress; disables the individual
        /// download button on every row that isn't currently downloading.
        /// </summary>
        public bool IsLocked
        {
            get => _isLocked;
            set { _isLocked = value; Notify(nameof(IsLocked)); }
        }

        // ── Transmission progress tracking ────────────────────────────────────

        /// <summary>Transmission torrent ID after a successful add.</summary>
        public int TorrentId { get; set; }

        /// <summary>Number of auto-retry attempts already made.</summary>
        public int RetryCount { get; set; }

        /// <summary>True while the torrent is active in Transmission (post-add, pre-complete).</summary>
        public bool IsInTransmission
        {
            get => _isInTransmission;
            set { _isInTransmission = value; Notify(nameof(IsInTransmission)); }
        }

        /// <summary>Download progress in the range [0, 1].</summary>
        public double Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                Notify(nameof(Progress));
                Notify(nameof(ProgressText));
            }
        }

        /// <summary>Current download speed in bytes/second.</summary>
        public long DownloadSpeedBytes
        {
            get => _downloadSpeedBytes;
            set
            {
                _downloadSpeedBytes = value;
                Notify(nameof(DownloadSpeedBytes));
                Notify(nameof(SpeedText));
            }
        }

        /// <summary>Estimated seconds remaining; -1 if unknown.</summary>
        public int DownloadEta
        {
            get => _downloadEta;
            set
            {
                _downloadEta = value;
                Notify(nameof(DownloadEta));
                Notify(nameof(EtaText));
            }
        }

        public string ProgressText => $"{_progress * 100:F0}%";

        public string SpeedText
        {
            get
            {
                if (_downloadSpeedBytes <= 0) return "";
                if (_downloadSpeedBytes < 1024 * 1024)
                    return $"{_downloadSpeedBytes / 1024.0:F1} KB/s";
                return $"{_downloadSpeedBytes / (1024.0 * 1024):F1} MB/s";
            }
        }

        public string EtaText
        {
            get
            {
                if (!_isInTransmission) return "";
                if (_downloadEta < 0) return "∞";
                return FormatTime(_downloadEta);
            }
        }

        private static string FormatTime(int sec)
        {
            if (sec >= 3600) { int h = sec / 3600; int m = (sec % 3600) / 60; return m > 0 ? $"{h}h {m}m" : $"{h}h"; }
            if (sec >= 60)   { int m = sec / 60;   int s = sec % 60;          return s > 0 ? $"{m}m {s:D2}s" : $"{m}m"; }
            return $"{sec}s";
        }

        private void Notify(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
