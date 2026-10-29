using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Kizuna
{
    public class CacheEntry
    {
        public string LastModifiedUtc          { get; set; }
        public string FranchiseName            { get; set; }
        public string SeasonName               { get; set; }
        public int    SeasonNumber             { get; set; } = -1;
        public string FranchiseSource          { get; set; } // "Api" or "Local"
        public string SeasonSource             { get; set; } // "Api" or "Local"
        public string CustomTargetDir          { get; set; }
        public string CustomTargetNameTemplate { get; set; }
    }

    public class LibraryFileCache
    {
        private static readonly string CacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kizuna", "library_cache.json");

        private Dictionary<string, CacheEntry> _data =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private LibraryFileCache() { }

        public static LibraryFileCache Load()
        {
            var c = new LibraryFileCache();
            try
            {
                if (File.Exists(CacheFile))
                {
                    var raw = JsonConvert.DeserializeObject<Dictionary<string, CacheEntry>>(
                        File.ReadAllText(CacheFile));
                    if (raw != null)
                        c._data = new Dictionary<string, CacheEntry>(raw, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
                File.WriteAllText(CacheFile, JsonConvert.SerializeObject(_data, Formatting.None));
            }
            catch { }
        }

        // entry is always populated if the path exists in cache (for custom-path preservation).
        // Returns true only when the cached metadata is fresh (lastModifiedUtc matches).
        public bool TryGetFresh(string filePath, string lastModifiedUtc, out CacheEntry entry)
        {
            if (!_data.TryGetValue(filePath, out entry))
            {
                entry = null;
                return false;
            }
            return entry.LastModifiedUtc == lastModifiedUtc;
        }

        public void Set(string filePath, CacheEntry entry) => _data[filePath] = entry;

        public void Remove(string filePath) => _data.Remove(filePath);

        public void SetCustomTarget(string filePath, string customDir, string customNameTemplate)
        {
            if (!_data.TryGetValue(filePath, out var e))
                _data[filePath] = e = new CacheEntry();
            e.CustomTargetDir          = customDir;
            e.CustomTargetNameTemplate = customNameTemplate;
        }

        // Removes entries for files that no longer exist on disk.
        public void PruneDeleted(HashSet<string> currentPaths)
        {
            var stale = new List<string>();
            foreach (var k in _data.Keys)
                if (!currentPaths.Contains(k)) stale.Add(k);
            foreach (var k in stale) _data.Remove(k);
        }

        // Removes all entries whose path starts with the given folder.
        public void PruneFolder(string folderPath)
        {
            string prefix = folderPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var stale = new List<string>();
            foreach (var k in _data.Keys)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    k.Equals(folderPath, StringComparison.OrdinalIgnoreCase))
                    stale.Add(k);
            foreach (var k in stale) _data.Remove(k);
        }
    }
}
