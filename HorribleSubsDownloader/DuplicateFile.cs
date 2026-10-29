using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Kizuna
{
    /// <summary>
    /// One physical file that is part of a <see cref="DuplicateGroup"/> (i.e. it has at
    /// least one byte-for-byte identical sibling). <see cref="Keep"/> decides whether it
    /// survives a cleanup; the visual colour follows it.
    /// </summary>
    public class DuplicateFile : INotifyPropertyChanged
    {
        public string FullPath  { get; set; }
        public long   SizeBytes { get; set; }

        /// <summary>Index of the added folder this file was found under (0 = topmost).
        /// Drives the default keep/delete decision.</summary>
        public int FolderOrder { get; set; }

        public string FileName  => System.IO.Path.GetFileName(FullPath);
        public string Directory => System.IO.Path.GetDirectoryName(FullPath);

        private bool _keep;
        public bool Keep
        {
            get => _keep;
            set
            {
                if (_keep == value) return;
                _keep = value;
                NotifyVisual();
            }
        }

        // True when the whole group is ambiguous (all copies live in the same folder, so
        // priority can't pick a keeper). Such files render orange and have no default
        // action — the user must decide via the resolve modal.
        private bool _ambiguous;
        public bool Ambiguous
        {
            get => _ambiguous;
            set
            {
                if (_ambiguous == value) return;
                _ambiguous = value;
                NotifyVisual();
            }
        }

        public string ActionText =>
            _ambiguous ? "UNDECIDED" : (_keep ? "KEEP" : "DELETE");

        public Brush ActionBrush =>
            _ambiguous ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x66))    // orange
                       : (_keep ? new SolidColorBrush(Color.FromRgb(0x57, 0xC4, 0x6E))    // green
                                : new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47)));  // red

        public Brush PathBrush =>
            _ambiguous ? new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x60))    // dim orange
                       : (_keep ? new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
                                : new SolidColorBrush(Color.FromRgb(0x99, 0x88, 0x88)));

        // Keeper (and ambiguous rows) sit flush left; delete-candidates are indented
        // underneath the keeper they will be removed in favour of.
        public Thickness RowMargin =>
            (_ambiguous || _keep) ? new Thickness(0, 1, 0, 1) : new Thickness(22, 1, 0, 1);

        private void NotifyVisual()
        {
            Notify(nameof(Keep));
            Notify(nameof(Ambiguous));
            Notify(nameof(ActionText));
            Notify(nameof(ActionBrush));
            Notify(nameof(PathBrush));
            Notify(nameof(RowMargin));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
