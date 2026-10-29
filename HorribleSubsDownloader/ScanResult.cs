using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;

namespace Kizuna
{
    public enum MetaSource { Unknown = 0, Local = 1, Api = 2 }

    public class ScanResult : INotifyPropertyChanged
    {
        private string     _status                  = "Scanning...";
        private string     _targetDir;
        private string     _targetFilename;
        private string     _customTargetDir;
        private string     _customTargetNameTemplate;
        private string     _franchiseName;
        private string     _seasonName;
        private bool       _isMoving;
        private MetaSource _franchiseSource  = MetaSource.Unknown;
        private MetaSource _seasonSource     = MetaSource.Unknown;
        private bool       _isResolvingMeta;
        private bool       _isFromCache;
        private bool       _isQueuedForMove;

        public string      SourceFilePath { get; set; }
        public string      Filename       { get; set; }
        public int         SeasonNumber   { get; set; } = -1;
        public int         EpisodeNumber  { get; set; } = -1;
        public string      EpisodeType    { get; set; }
        public LibraryRule Rule           { get; set; }

        public string FranchiseName
        {
            get => _franchiseName;
            set { _franchiseName = value; Notify(nameof(FranchiseName)); }
        }

        public string SeasonName
        {
            get => _seasonName;
            set { _seasonName = value; Notify(nameof(SeasonName)); }
        }

        // Whether franchise/season came from the API or was extracted locally
        public MetaSource FranchiseSource
        {
            get => _franchiseSource;
            set { _franchiseSource = value; Notify(nameof(FranchiseSource)); Notify(nameof(FranchiseForeground)); Notify(nameof(FranchiseTooltip)); }
        }

        public MetaSource SeasonSource
        {
            get => _seasonSource;
            set { _seasonSource = value; Notify(nameof(SeasonSource)); Notify(nameof(SeasonForeground)); Notify(nameof(SeasonTooltip)); }
        }

        public bool IsResolvingMeta
        {
            get => _isResolvingMeta;
            set { _isResolvingMeta = value; Notify(nameof(IsResolvingMeta)); }
        }

        // True when the franchise/season shown was loaded from the local cache and
        // has not (yet) been re-resolved against the API this session. Cleared the
        // moment a fresh resolve completes.
        public bool IsFromCache
        {
            get => _isFromCache;
            set
            {
                _isFromCache = value;
                Notify(nameof(IsFromCache));
                Notify(nameof(CacheBadgeTooltip));
            }
        }

        public string CacheBadgeTooltip =>
            _isFromCache ? "Loaded from cache — not yet re-resolved this session" : null;

        public Brush  FranchiseForeground => SourceBrush(_franchiseSource);
        public string FranchiseTooltip    => SourceTooltip(_franchiseSource);
        public Brush  SeasonForeground    => SourceBrush(_seasonSource);
        public string SeasonTooltip       => SourceTooltip(_seasonSource);

        // Rule-computed directory
        public string TargetDir
        {
            get => _targetDir;
            set { _targetDir = value; NotifyTarget(); }
        }

        // Rule-computed filename (clean or original)
        public string TargetFilename
        {
            get => _targetFilename;
            set { _targetFilename = value; NotifyTarget(); }
        }

        // User override: directory only (null = use rule-computed)
        public string CustomTargetDir
        {
            get => _customTargetDir;
            set { _customTargetDir = value; NotifyTarget(); }
        }

        // User override: filename template with %e/%t placeholders (null = use rule-computed)
        public string CustomTargetNameTemplate
        {
            get => _customTargetNameTemplate;
            set { _customTargetNameTemplate = value; NotifyTarget(); }
        }

        public bool HasCustomDir  => !string.IsNullOrEmpty(_customTargetDir);
        public bool HasCustomName => !string.IsNullOrEmpty(_customTargetNameTemplate);
        public bool HasCustomPath => HasCustomDir || HasCustomName;

        public string EffectiveTargetDir  => HasCustomDir  ? _customTargetDir  : _targetDir;
        public string EffectiveTargetName => HasCustomName ? ExpandPlaceholders(_customTargetNameTemplate) : _targetFilename;

        public string EffectiveTargetPath
        {
            get
            {
                var dir  = EffectiveTargetDir;
                var name = EffectiveTargetName;
                return dir != null && name != null ? Path.Combine(dir, name) : null;
            }
        }

        public string TargetPath        => EffectiveTargetPath;
        public string DisplayTargetPath => EffectiveTargetPath;

        // True from the moment the user initiates a move on this item until that move
        // finishes (success, skip, or error). Drives the Move Queue tab — it lists only
        // items the user has actually told to move that aren't done yet.
        public bool IsQueuedForMove
        {
            get => _isQueuedForMove;
            set { _isQueuedForMove = value; Notify(nameof(IsQueuedForMove)); }
        }

        public bool IsMoving
        {
            get => _isMoving;
            set { _isMoving = value; Notify(nameof(IsMoving)); }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                Notify(nameof(Status));
                Notify(nameof(StatusBrush));
            }
        }

        public Brush StatusBrush
        {
            get
            {
                if (_status?.StartsWith("Error") == true)
                    return new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                if (_status == "Moved")
                    return new SolidColorBrush(Color.FromRgb(0x57, 0xC4, 0x6E));
                if (_status == "No Rule")
                    return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                if (_status == "Moving...")
                    return new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x66));
                return new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
            }
        }

        private static Brush SourceBrush(MetaSource src)
        {
            switch (src)
            {
                case MetaSource.Api:   return new SolidColorBrush(Color.FromRgb(0x57, 0xC4, 0x6E)); // green
                case MetaSource.Local: return new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x66)); // amber
                default:               return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)); // gray
            }
        }

        private static string SourceTooltip(MetaSource src)
        {
            switch (src)
            {
                case MetaSource.Api:   return "Matched via API";
                case MetaSource.Local: return "Auto-detected from filename";
                default:               return null;
            }
        }

        private string ExpandPlaceholders(string template)
        {
            if (string.IsNullOrEmpty(template)) return template;
            string s = template;
            s = s.Replace("%e", EpisodeNumber >= 0 ? EpisodeNumber.ToString("D2") : "");
            s = s.Replace("%t", EpisodeType   ?? "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*-\s*$", "").Trim();
            return s;
        }

        private void NotifyTarget()
        {
            Notify(nameof(TargetDir));
            Notify(nameof(TargetFilename));
            Notify(nameof(CustomTargetDir));
            Notify(nameof(CustomTargetNameTemplate));
            Notify(nameof(HasCustomDir));
            Notify(nameof(HasCustomName));
            Notify(nameof(HasCustomPath));
            Notify(nameof(EffectiveTargetDir));
            Notify(nameof(EffectiveTargetName));
            Notify(nameof(EffectiveTargetPath));
            Notify(nameof(TargetPath));
            Notify(nameof(DisplayTargetPath));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
