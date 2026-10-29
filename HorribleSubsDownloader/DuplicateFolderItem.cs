using System.ComponentModel;

namespace Kizuna
{
    /// <summary>
    /// A folder added to the Duplicate Finder. Position in the list is the priority:
    /// the topmost folder keeps its copy of a duplicate by default, folders below it
    /// have their copies deleted.
    /// </summary>
    internal sealed class DuplicateFolderItem : INotifyPropertyChanged
    {
        public string Path { get; }

        private bool _isAvailable = true;
        public bool IsAvailable
        {
            get => _isAvailable;
            set
            {
                if (_isAvailable == value) return;
                _isAvailable = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAvailable)));
            }
        }

        public DuplicateFolderItem(string path) { Path = path; }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
