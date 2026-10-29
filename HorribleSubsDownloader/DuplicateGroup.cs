using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Kizuna
{
    /// <summary>
    /// A set of two or more byte-for-byte identical files (same full content hash).
    /// </summary>
    public class DuplicateGroup : INotifyPropertyChanged
    {
        public string Hash      { get; set; }
        public long   SizeBytes { get; set; }

        public ObservableCollection<DuplicateFile> Files { get; }
            = new ObservableCollection<DuplicateFile>();

        public DuplicateGroup()
        {
            Files.CollectionChanged += (s, e) => NotifyCounts();
        }

        /// <summary>Representative file name (first copy).</summary>
        public string FileName => Files.Count > 0 ? Files[0].FileName : "";

        public int CopyCount => Files.Count;

        public string SizeText => FormatBytes(SizeBytes);

        private bool _isAmbiguous;
        /// <summary>True when every copy lives in the same folder, so folder priority
        /// can't choose a keeper. No default action is applied — the user must resolve
        /// the group manually.</summary>
        public bool IsAmbiguous
        {
            get => _isAmbiguous;
            private set { _isAmbiguous = value; Notify(nameof(IsAmbiguous)); }
        }

        /// <summary>Applies the "keep the first listed copy, delete the rest" fallback used
        /// when a group has no clear winner (all copies in the same folder). Confirmed via a
        /// warning modal before any files are removed.</summary>
        public void ApplyKeepFirst()
        {
            for (int i = 0; i < Files.Count; i++)
            {
                Files[i].Ambiguous = false;
                Files[i].Keep      = i == 0;
            }
            NotifyCounts();
        }

        /// <summary>Potential bytes freed. For a decided group this is the copies marked
        /// for deletion; for an ambiguous group it's the space freed once the user keeps
        /// a single copy.</summary>
        public long ReclaimableBytes => _isAmbiguous
            ? (CopyCount - 1) * SizeBytes
            : Files.Count(f => !f.Keep) * SizeBytes;

        public string ReclaimableText => FormatBytes(ReclaimableBytes);

        public string Summary
        {
            get
            {
                if (_isAmbiguous)
                    return $"{CopyCount} copies · same folder — choose one to keep";
                int keep = Files.Count(f => f.Keep);
                int del  = Files.Count - keep;
                return $"{CopyCount} copies · keep {keep} · delete {del}";
            }
        }

        /// <summary>Applies the default decision. When the copies span more than one
        /// folder, the copy in the highest-priority folder (lowest FolderOrder, ties
        /// broken by path) is kept and the rest are marked for deletion. When every copy
        /// is in the same folder the group is flagged ambiguous: all copies render orange
        /// and nothing is kept/deleted until the user decides.</summary>
        public void ApplyDefaultKeep()
        {
            bool ambiguous = Files.Count > 1
                             && Files.All(f => f.FolderOrder == Files[0].FolderOrder);
            IsAmbiguous = ambiguous;

            if (ambiguous)
            {
                foreach (var f in Files) { f.Keep = false; f.Ambiguous = true; }
            }
            else
            {
                DuplicateFile winner = Files
                    .OrderBy(f => f.FolderOrder)
                    .ThenBy(f => f.FullPath, System.StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                foreach (var f in Files)
                {
                    f.Ambiguous = false;
                    f.Keep      = ReferenceEquals(f, winner);
                }
            }

            NotifyCounts();
        }

        /// <summary>Clears the ambiguous flag once the user has made an explicit choice.</summary>
        public void MarkDecided()
        {
            IsAmbiguous = false;
            foreach (var f in Files) f.Ambiguous = false;
            NotifyCounts();
        }

        public void NotifyCounts()
        {
            Notify(nameof(FileName));
            Notify(nameof(CopyCount));
            Notify(nameof(ReclaimableBytes));
            Notify(nameof(ReclaimableText));
            Notify(nameof(Summary));
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
