using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Kizuna
{
    public class HashCacheEntry
    {
        public long   Size            { get; set; }
        public string LastModifiedUtc { get; set; }
        public string QuickHash       { get; set; }
        public string FullHash        { get; set; }
    }

    /// <summary>
    /// Persistent cache of file content hashes for the Duplicate Finder, keyed by full
    /// path. A cached hash is only reused when the file's size and last-write time still
    /// match, so unchanged files never get re-hashed — new and modified files are the
    /// only ones that require I/O on a re-scan.
    /// </summary>
    public class DuplicateHashCache
    {
        private static readonly string CacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kizuna", "duplicate_hashes.json");

        private Dictionary<string, HashCacheEntry> _data =
            new Dictionary<string, HashCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private DuplicateHashCache() { }

        public static DuplicateHashCache Load()
        {
            var c = new DuplicateHashCache();
            try
            {
                if (File.Exists(CacheFile))
                {
                    var raw = JsonConvert.DeserializeObject<Dictionary<string, HashCacheEntry>>(
                        File.ReadAllText(CacheFile));
                    if (raw != null)
                        c._data = new Dictionary<string, HashCacheEntry>(raw, StringComparer.OrdinalIgnoreCase);
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

        /// <summary>Returns the cached entry if it is still valid for the given file
        /// stamp, otherwise null.</summary>
        public HashCacheEntry GetFresh(string path, long size, string lastModifiedUtc)
        {
            if (_data.TryGetValue(path, out var e) &&
                e.Size == size && e.LastModifiedUtc == lastModifiedUtc)
                return e;
            return null;
        }

        public HashCacheEntry GetOrCreate(string path, long size, string lastModifiedUtc)
        {
            var e = GetFresh(path, size, lastModifiedUtc);
            if (e != null) return e;
            e = new HashCacheEntry { Size = size, LastModifiedUtc = lastModifiedUtc };
            _data[path] = e;
            return e;
        }

        public void Remove(string path) => _data.Remove(path);
    }
}
