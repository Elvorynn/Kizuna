using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kizuna
{
    public class TargetResolution
    {
        public string       TargetDir      { get; set; }
        public string       FranchiseDir   { get; set; }
        public bool         IsFlat         { get; set; }
        public bool         NeedsMigration { get; set; }
        public FranchiseInfo ExistingInfo  { get; set; }
    }

    public static class LibraryScanner
    {
        private static readonly string[] VideoExtensions =
            { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".ts", ".m2ts" };

        public static bool IsVideoFile(string path)
            => VideoExtensions.Contains(
                   Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<string> FindVideoFiles(string folder, bool recursive)
        {
            if (!Directory.Exists(folder)) return Enumerable.Empty<string>();
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            try
            {
                return Directory.GetFiles(folder, "*.*", option)
                    .Where(f => IsVideoFile(f));
            }
            catch { return Enumerable.Empty<string>(); }
        }

        // "[SubsPlease] Quanzhi Fashi S7 - 04 (1080p) [9BA9BD38].mkv" → "Quanzhi Fashi S7 - 04.mkv"
        public static string CleanFilename(string filename)
        {
            string ext  = Path.GetExtension(filename);
            string name = Path.GetFileNameWithoutExtension(filename);

            name = Regex.Replace(name, @"^\[[^\]]*\]\s*", "");
            name = Regex.Replace(name,
                @"\s*[\(\[](1080p|720p|480p|2160p|4K|HEVC|H\.?264|H\.?265|x264|x265|AVC|AAC|FLAC)[^\)\]]*[\)\]].*$",
                "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s*\[[0-9A-Fa-f]{8}\].*$", "");
            name = Regex.Replace(name, @"(\d+)v\d+\b", "$1", RegexOptions.IgnoreCase);

            return name.Trim() + ext;
        }

        // "Quanzhi Fashi" from "[SubsPlease] Quanzhi Fashi S7 - 04 (1080p) [9BA9BD38].mkv"
        public static string ExtractBaseName(string filename)
        {
            string name = Path.GetFileNameWithoutExtension(CleanFilename(filename));

            name = Regex.Replace(name, @"\s+S\d+\s*-\s*\d+.*$",  "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s+S\d+E\d+.*$",         "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s+-\s*\d+(?:\s+.*)?$",  "");
            name = Regex.Replace(name, @"\s+S\d+$",                "", RegexOptions.IgnoreCase);

            return name.Trim();
        }

        public static LibraryRule FindMatchingRule(string filePath, IList<LibraryRule> rules)
        {
            if (rules == null || rules.Count == 0) return null;
            string dir = Path.GetDirectoryName(filePath) ?? "";

            foreach (var rule in rules)
            {
                if (string.IsNullOrEmpty(rule.SourcePath)) continue;
                string src = rule.SourcePath.TrimEnd('\\', '/');

                bool match = rule.MatchSubfolders
                    ? dir.StartsWith(src, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(dir, src, StringComparison.OrdinalIgnoreCase);

                if (match) return rule;
            }
            return null;
        }

        // Resolves the effective target directory for a file.
        // For CreateSeasonFolderIfMultiple rules this checks the filesystem: reads the
        // _kizuna.info sentinel in the franchise folder to decide flat vs. season subfolder.
        public static TargetResolution ResolveTargetDirectory(
            LibraryRule rule, string franchiseName, string seasonName, int seasonNumber)
        {
            string target      = rule.TargetBasePath;
            string franchiseDir = null;

            if (rule.CreateFranchiseFolder && !string.IsNullOrWhiteSpace(franchiseName))
            {
                franchiseDir = Path.Combine(target, SanitizeFolderName(franchiseName));
                target       = franchiseDir;
            }

            if (rule.CreateFranchiseFolder && rule.CreateSeasonFolderIfMultiple
                && franchiseDir != null && !string.IsNullOrWhiteSpace(seasonName))
            {
                // Franchise folder doesn't exist yet → first file, go flat
                if (!Directory.Exists(franchiseDir))
                    return Flat(franchiseDir, franchiseDir);

                string infoPath = FranchiseInfo.GetPath(franchiseDir);
                if (File.Exists(infoPath))
                {
                    var info = FranchiseInfo.Read(franchiseDir);
                    if (info.SeasonMatches(seasonName, seasonNumber))
                        return Flat(franchiseDir, franchiseDir, existingInfo: info);

                    // New season detected: this file goes into a season subfolder
                    // and old flat files need migration to their own season subfolder
                    string newFolder = MakeSeasonFolderName(seasonName, seasonNumber);
                    return new TargetResolution
                    {
                        TargetDir      = Path.Combine(franchiseDir, SanitizeFolderName(newFolder)),
                        FranchiseDir   = franchiseDir,
                        IsFlat         = false,
                        NeedsMigration = true,
                        ExistingInfo   = info
                    };
                }

                // No .info but subfolders already exist → always use a season subfolder
                if (Directory.GetDirectories(franchiseDir).Length > 0)
                {
                    string folder = MakeSeasonFolderName(seasonName, seasonNumber);
                    return new TargetResolution
                    {
                        TargetDir    = Path.Combine(franchiseDir, SanitizeFolderName(folder)),
                        FranchiseDir = franchiseDir,
                        IsFlat       = false
                    };
                }

                // No .info, no subfolders → first files, go flat
                return Flat(franchiseDir, franchiseDir);
            }

            return Flat(target, franchiseDir);
        }

        // Convenience wrapper — returns only the directory string (for callers that don't
        // need migration info, e.g. the scan preview when no adaptive logic applies).
        public static string ComputeTargetDirectory(
            LibraryRule rule, string franchiseName, string seasonName, int seasonNumber)
            => ResolveTargetDirectory(rule, franchiseName, seasonName, seasonNumber).TargetDir;

        public static string MakeSeasonFolderName(string seasonName, int seasonNumber)
            => seasonNumber > 0 ? $"{seasonName} (S{seasonNumber:D2})" : seasonName;

        // Returns season name + number from a filename, or (null, -1) if not found.
        // Handles patterns like "S07", "S7 -", "Season 7", "2nd Season", etc.
        public static (string name, int number) ExtractSeasonInfo(string filename)
        {
            string raw = Path.GetFileNameWithoutExtension(filename);

            // "2nd Season", "3rd Season", etc.
            var mOrdinal = Regex.Match(raw,
                @"\b(\d+)(?:st|nd|rd|th)\s+Season\b", RegexOptions.IgnoreCase);
            if (mOrdinal.Success)
            {
                int n = int.Parse(mOrdinal.Groups[1].Value);
                return ($"Season {n}", n);
            }

            // "Season 7" / "Season7"
            var mWord = Regex.Match(raw, @"\bSeason\s*(\d{1,2})\b", RegexOptions.IgnoreCase);
            if (mWord.Success)
            {
                int n = int.Parse(mWord.Groups[1].Value);
                return ($"Season {n}", n);
            }

            // "S07", "S7", "S7E04", "S07 -"  — must be preceded by space/start/bracket
            var mS = Regex.Match(raw, @"(?:^|[\s\[\(])S(\d{1,2})(?:\s|E\d|\s*-|$)",
                RegexOptions.IgnoreCase);
            if (mS.Success)
            {
                int n = int.Parse(mS.Groups[1].Value);
                if (n > 1) return ($"Season {n}", n);  // Season 1 is default, don't force-show it
            }

            return (null, -1);
        }

        // Returns OVA / ONA / OAD / SPECIAL / MOVIE if detected in the filename, else null.
        public static string ExtractEpisodeType(string filename)
        {
            string name = Path.GetFileNameWithoutExtension(filename);
            var m = Regex.Match(name, @"\b(OVA|ONA|OAD|Specials?|Movie)\b", RegexOptions.IgnoreCase);
            return m.Success ? m.Value.ToUpperInvariant() : null;
        }

        // Returns the episode number from a typical fansub filename, or -1 if not found.
        public static int ExtractEpisodeNumber(string filename)
        {
            string name = Path.GetFileNameWithoutExtension(filename);
            name = Regex.Replace(name, @"^\[[^\]]*\]\s*", "");
            name = Regex.Replace(name, @"(\d+)v\d+\b", "$1", RegexOptions.IgnoreCase);

            var m = Regex.Match(name, @"S\d+E(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value);

            m = Regex.Match(name, @"S\d+\s*-\s*(\d{1,4})\b", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value);

            var hits = Regex.Matches(name, @"\s+-\s*(\d{1,4})\s*(?:\[|\(|$)");
            if (hits.Count > 0) return int.Parse(hits[hits.Count - 1].Groups[1].Value);

            return -1;
        }

        // Kept for callers that used the old name
        public static string ExtractType(string filename) => ExtractEpisodeType(filename);

        public static string SanitizeFolderName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim('.', ' ');
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static TargetResolution Flat(string targetDir, string franchiseDir,
            FranchiseInfo existingInfo = null)
            => new TargetResolution
            {
                TargetDir    = targetDir,
                FranchiseDir = franchiseDir,
                IsFlat       = true,
                ExistingInfo = existingInfo
            };
    }
}
