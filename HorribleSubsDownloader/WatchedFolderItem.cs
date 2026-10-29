using System.ComponentModel;

namespace Kizuna
{
    internal sealed class WatchedFolderItem : INotifyPropertyChanged
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

        public WatchedFolderItem(string path) { Path = path; }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
