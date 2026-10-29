using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahApps.Metro.Controls;

namespace Kizuna
{
    /// <summary>
    /// Warning + preview modal shown before cleaning up groups. For groups with no clear
    /// winner (all copies in the same folder) it applies the "keep the first listed file,
    /// delete the rest" fallback and shows the resulting keep/delete outcome so the user
    /// can review it. Already-decided groups are shown as-is. Cancelling restores the
    /// original state of any group it touched.
    /// </summary>
    public partial class DuplicateDefaultResolveWindow : MetroWindow
    {
        private readonly IList<DuplicateGroup> _groups;
        private readonly Dictionary<DuplicateFile, bool> _snapKeep;
        private readonly Dictionary<DuplicateFile, bool> _snapAmbiguous;

        public DuplicateDefaultResolveWindow(IList<DuplicateGroup> groups)
        {
            InitializeComponent();

            _groups        = groups;
            _snapKeep      = new Dictionary<DuplicateFile, bool>();
            _snapAmbiguous = new Dictionary<DuplicateFile, bool>();

            bool anyAmbiguous = false;
            foreach (var g in groups)
            {
                foreach (var f in g.Files)
                {
                    _snapKeep[f]      = f.Keep;
                    _snapAmbiguous[f] = f.Ambiguous;
                }

                // Apply the keep-first fallback to undecided groups so the preview reflects
                // exactly what a cleanup would do. IsAmbiguous is left set so the badge shows.
                if (g.IsAmbiguous)
                {
                    anyAmbiguous = true;
                    for (int i = 0; i < g.Files.Count; i++)
                    {
                        g.Files[i].Ambiguous = false;
                        g.Files[i].Keep      = i == 0;
                    }
                    g.NotifyCounts();
                }
            }

            WarningBanner.Visibility = anyAmbiguous ? Visibility.Visible : Visibility.Collapsed;
            LstGroups.ItemsSource    = groups;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int  del   = _groups.Sum(g => g.Files.Count(f => !f.Keep));
            long freed = _groups.Sum(g => g.Files.Count(f => !f.Keep) * g.SizeBytes);
            BtnConfirm.Content = del == 1 ? "Delete 1 file" : $"Delete {del} files";
            TxtSummary.Text =
                $"Keeping {_groups.Sum(g => g.Files.Count(f => f.Keep))} file(s), " +
                $"deleting {del} across {_groups.Count} group(s) — reclaiming {DuplicateGroup.FormatBytes(freed)}.";
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            var f = (sender as FrameworkElement)?.DataContext as DuplicateFile;
            if (f == null) return;
            try { MediaLauncher.Open(f.FullPath); } catch { /* surfaced by the main log on the grid */ }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in _groups) g.MarkDecided();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in _groups)
            {
                foreach (var f in g.Files)
                {
                    if (_snapKeep.TryGetValue(f, out bool k))      f.Keep      = k;
                    if (_snapAmbiguous.TryGetValue(f, out bool a)) f.Ambiguous = a;
                }
                g.NotifyCounts();
            }
            DialogResult = false;
            Close();
        }
    }
}
