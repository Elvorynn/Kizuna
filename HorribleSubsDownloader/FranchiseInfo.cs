using System;
using System.Collections.Generic;
using System.IO;

namespace Kizuna
{
    /// <summary>
    /// Reads and writes the _kizuna.info sentinel file placed in a franchise folder
    /// when files are stored flat (no season subfolder yet).
    /// Format: key=value lines, one "File=" entry per tracked filename.
    /// </summary>
    public class FranchiseInfo
    {
        public const string FileName = "_kizuna.info";

        public string       SeasonName   { get; set; }
        public int          SeasonNumber { get; set; } = -1;
        public List<string> Files        { get; set; } = new List<string>();

        public static string GetPath(string franchiseDir)
            => Path.Combine(franchiseDir, FileName);

        public static bool ExistsIn(string franchiseDir)
            => File.Exists(GetPath(franchiseDir));

        public static FranchiseInfo Read(string franchiseDir)
        {
            var    info = new FranchiseInfo();
            string path = GetPath(franchiseDir);
            if (!File.Exists(path)) return info;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if      (key.Equals("SeasonName",   StringComparison.OrdinalIgnoreCase))
                        info.SeasonName = val;
                    else if (key.Equals("SeasonNumber", StringComparison.OrdinalIgnoreCase))
                    { if (int.TryParse(val, out int n)) info.SeasonNumber = n; }
                    else if (key.Equals("File",         StringComparison.OrdinalIgnoreCase))
                        info.Files.Add(val);
                }
            }
            catch { }
            return info;
        }

        public void Write(string franchiseDir)
        {
            try
            {
                Directory.CreateDirectory(franchiseDir);
                var lines = new List<string>();
                if (SeasonName   != null) lines.Add($"SeasonName={SeasonName}");
                if (SeasonNumber >= 0)    lines.Add($"SeasonNumber={SeasonNumber}");
                foreach (var f in Files) lines.Add($"File={f}");
                File.WriteAllLines(GetPath(franchiseDir), lines);
            }
            catch { }
        }

        // Returns true when the given season is considered the same as what this info tracks.
        // Unknown seasons (null/empty or -1) are treated as a match to avoid false migrations.
        public bool SeasonMatches(string seasonName, int seasonNumber)
        {
            if (string.IsNullOrEmpty(SeasonName) || string.IsNullOrEmpty(seasonName))
                return true;
            bool nameMatch = string.Equals(SeasonName, seasonName, StringComparison.OrdinalIgnoreCase);
            bool numMatch  = SeasonNumber < 0 || seasonNumber < 0 || SeasonNumber == seasonNumber;
            return nameMatch && numMatch;
        }
    }
}
