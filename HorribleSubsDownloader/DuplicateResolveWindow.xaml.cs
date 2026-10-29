using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using MahApps.Metro.Controls;

namespace Kizuna
{
    /// <summary>
    /// Modal that lets the user pick which copies of a duplicate group to keep.
    /// Edits the group's <see cref="DuplicateFile.Keep"/> flags live; if the user
    /// cancels, the original flags are restored.
    /// </summary>
    public partial class DuplicateResolveWindow : MetroWindow
    {
        private readonly DuplicateGroup _group;
        private readonly Dictionary<DuplicateFile, bool> _originalKeep;
        private readonly Dictionary<DuplicateFile, bool> _originalAmbiguous;

        public DuplicateResolveWindow(DuplicateGroup group)
        {
            InitializeComponent();

            _group             = group;
            _originalKeep      = group.Files.ToDictionary(f => f, f => f.Keep);
            _originalAmbiguous = group.Files.ToDictionary(f => f, f => f.Ambiguous);

            // While editing, drop the ambiguous flag so ticking a copy immediately shows
            // it green (kept) rather than staying orange. Restored on cancel.
            foreach (var f in group.Files)
                f.Ambiguous = false;

            TxtHeader.Text      = $"{group.FileName}  —  {group.SizeText} × {group.CopyCount} copies";
            LstFiles.ItemsSource = group.Files;

            foreach (var f in group.Files)
                f.PropertyChanged += File_PropertyChanged;

            UpdateSummary();
        }

        private void File_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DuplicateFile.Keep))
                UpdateSummary();
        }

        private void UpdateSummary()
        {
            int keep = _group.Files.Count(f => f.Keep);
            int del  = _group.Files.Count - keep;
            long freed = del * _group.SizeBytes;

            TxtSummary.Text = del == 0
                ? "Nothing will be deleted — tick fewer copies to reclaim space."
                : $"Will delete {del} file(s) and reclaim {DuplicateGroup.FormatBytes(freed)}.";
        }

        private void Unhook()
        {
            foreach (var f in _group.Files)
                f.PropertyChanged -= File_PropertyChanged;
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            var f = (sender as FrameworkElement)?.DataContext as DuplicateFile;
            if (f == null) return;
            try { MediaLauncher.Open(f.FullPath); } catch { /* best-effort */ }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!_group.Files.Any(f => f.Keep))
            {
                MessageBox.Show(this,
                    "Tick at least one copy to keep — the last copy is never deleted.",
                    "Nothing kept", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Unhook();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Unhook();
            // Restore original keep + ambiguous flags
            foreach (var f in _group.Files)
            {
                if (_originalKeep.TryGetValue(f, out bool k))      f.Keep      = k;
                if (_originalAmbiguous.TryGetValue(f, out bool a)) f.Ambiguous = a;
            }
            DialogResult = false;
            Close();
        }
    }
}
