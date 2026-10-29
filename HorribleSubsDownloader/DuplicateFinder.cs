using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Kizuna
{
    /// <summary>
    /// Finds byte-for-byte duplicate files across a set of folders as cheaply as
    /// possible. The work is staged so the vast majority of files are eliminated
    /// without ever being fully read:
    ///
    ///   1. Enumerate all files and group by size — a file with a unique size cannot
    ///      have a duplicate, so those are dropped immediately (no hashing).
    ///   2. For the remaining same-size candidates, compute a cheap "quick hash" of the
    ///      first and last 64 KB. Groups that don't collide are dropped.
    ///   3. Only the survivors get a full-content hash to confirm the match.
    ///
    /// Every hash is cached (see <see cref="DuplicateHashCache"/>) keyed by size +
    /// last-write time, so an unchanged file is never hashed twice across scans — only
    /// new or modified files cost any I/O. Hashing runs sequentially with sequential-scan
    /// hints and below-normal thread priority to keep the machine responsive.
    /// </summary>
    public static class DuplicateFinder
    {
        private const int QuickHashChunk = 64 * 1024;      // 64 KB from each end
        private const int ReadBuffer     = 1024 * 1024;    // 1 MB streaming buffer

        // NAS recycle-bin / system folders whose contents must never be treated as
        // duplicates (Synology / QNAP). A file is skipped if any folder in its path
        // matches one of these names.
        private static readonly string[] IgnoredDirSegments = { "@Recycle", "#recycle", "@eaDir" };

        private static bool IsInIgnoredFolder(string path)
        {
            foreach (var seg in IgnoredDirSegments)
                if (path.IndexOf("\\" + seg + "\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        public class ScanProgress
        {
            public string Phase          { get; set; }
            public int    FilesProcessed { get; set; }
            public int    FilesTotal     { get; set; }
            public double Fraction       { get; set; }   // 0..1
            public string CurrentFile    { get; set; }
            /// <summary>When set, the UI appends this line to the event log.</summary>
            public string LogMessage     { get; set; }
        }

        private class Candidate
        {
            public string        Path;
            public long          Size;
            public int           FolderOrder;
            public string        LastModifiedUtc;
            public HashCacheEntry Cache;
        }

        /// <param name="folders">Existing, reachable folders in priority order.</param>
        public static Task<List<DuplicateGroup>> FindAsync(
            IReadOnlyList<string> folders,
            DuplicateHashCache cache,
            IProgress<ScanProgress> progress,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var prevPriority = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                try { return Find(folders, cache, progress, ct); }
                finally { Thread.CurrentThread.Priority = prevPriority; }
            }, ct);
        }

        private static List<DuplicateGroup> Find(
            IReadOnlyList<string> folders,
            DuplicateHashCache cache,
            IProgress<ScanProgress> progress,
            CancellationToken ct)
        {
            // ── Phase 1: enumerate + group by size ──────────────────────────────
            progress?.Report(new ScanProgress
            {
                Phase      = "Enumerating files…",
                Fraction   = 0,
                LogMessage = $"Phase 1/3 — enumerating files across {folders.Count} folder(s)…"
            });

            var byPath  = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            for (int order = 0; order < folders.Count; order++)
            {
                ct.ThrowIfCancellationRequested();
                string root = folders[order];
                progress?.Report(new ScanProgress
                {
                    Phase       = "Enumerating…",
                    CurrentFile = root,
                    LogMessage  = $"  Scanning folder [{order + 1}]: {root}"
                });

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
                catch (Exception ex)
                {
                    progress?.Report(new ScanProgress
                    {
                        LogMessage = $"Warning: Could not read {root}: {ex.Message}"
                    });
                    continue;
                }

                int folderCount = 0, folderSkipped = 0;
                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (byPath.ContainsKey(path)) continue;   // seen under a higher folder
                    if (IsInIgnoredFolder(path)) { folderSkipped++; continue; }  // @Recycle etc.
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Length <= 0) continue;         // skip empty files
                        byPath[path] = new Candidate
                        {
                            Path            = path,
                            Size            = fi.Length,
                            FolderOrder     = order,
                            LastModifiedUtc = fi.LastWriteTimeUtc.ToString("o")
                        };
                        folderCount++;
                    }
                    catch { }
                }

                string skipNote = folderSkipped > 0 ? $" ({folderSkipped} skipped in recycle/system folders)" : "";
                progress?.Report(new ScanProgress
                {
                    LogMessage = $"    → {folderCount} usable file(s) in this folder{skipNote}"
                });
            }

            var bySize = byPath.Values
                .GroupBy(c => c.Size)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g)
                .ToList();

            progress?.Report(new ScanProgress
            {
                LogMessage = $"  {byPath.Count} file(s) total → {bySize.Count} share a size and need checking " +
                             $"({byPath.Count - bySize.Count} skipped as unique-size)."
            });

            if (bySize.Count == 0)
            {
                progress?.Report(new ScanProgress { Fraction = 1, LogMessage = "No same-size files — no duplicates possible." });
                return new List<DuplicateGroup>();
            }

            // ── Phase 2: quick hash (first + last 64 KB) ────────────────────────
            int total = bySize.Count;
            progress?.Report(new ScanProgress
            {
                Phase      = "Quick-hashing…",
                LogMessage = $"Phase 2/3 — quick-hashing {total} candidate(s) (first + last 64 KB each)…"
            });

            // Reports are throttled by time (not per file) so the UI thread isn't flooded
            // with thousands of dispatcher posts — that backlog is what made the log and
            // window appear to freeze mid-scan.
            int quickComputed = 0, quickCached = 0;
            var sw = Stopwatch.StartNew();
            long lastBar = -10000, lastLog = -10000;
            for (int i = 0; i < bySize.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = bySize[i];
                c.Cache = cache.GetOrCreate(c.Path, c.Size, c.LastModifiedUtc);

                bool compute = c.Cache.QuickHash == null;
                if (compute)
                {
                    try { c.Cache.QuickHash = ComputeQuickHash(c.Path, c.Size, ct); }
                    catch { c.Cache.QuickHash = "?" + Guid.NewGuid().ToString("N"); } // unreadable → unique
                    quickComputed++;
                }
                else quickCached++;

                long now    = sw.ElapsedMilliseconds;
                bool last   = i == bySize.Count - 1;
                bool barDue = last || now - lastBar >= 100;
                bool logDue = now - lastLog >= 600;
                if (barDue || logDue)
                {
                    var sp = new ScanProgress
                    {
                        Phase          = "Quick-hashing…",
                        FilesProcessed = i + 1,
                        FilesTotal     = total,
                        Fraction       = 0.15 * (i + 1) / total,
                        CurrentFile    = c.Path
                    };
                    if (logDue) { sp.LogMessage = $"  quick-hashed {i + 1}/{total}…"; lastLog = now; }
                    progress?.Report(sp);
                    lastBar = now;
                }
            }

            progress?.Report(new ScanProgress
            {
                LogMessage = $"  quick hash done — {quickComputed} newly hashed, {quickCached} reused from cache."
            });

            var quickGroups = bySize
                .GroupBy(c => c.Size + ":" + c.Cache.QuickHash)
                .Where(g => g.Count() > 1)
                .ToList();

            var toFull = quickGroups.SelectMany(g => g).ToList();
            progress?.Report(new ScanProgress
            {
                LogMessage = $"  narrowed to {toFull.Count} file(s) in {quickGroups.Count} potential group(s)."
            });

            if (toFull.Count == 0)
            {
                progress?.Report(new ScanProgress { Fraction = 1, LogMessage = "No matches survived the quick hash — no duplicates." });
                return new List<DuplicateGroup>();
            }

            // ── Phase 3: full-content hash to confirm ───────────────────────────
            total = toFull.Count;
            progress?.Report(new ScanProgress
            {
                Phase      = "Full hashing…",
                LogMessage = $"Phase 3/3 — full-hashing {total} file(s) to confirm identical content…"
            });

            int fullComputed = 0, fullCached = 0;
            sw.Restart();
            lastBar = -10000; lastLog = -10000;
            for (int i = 0; i < toFull.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = toFull[i];
                bool compute = c.Cache.FullHash == null;

                // Reported before the (potentially long) read so the log shows which file
                // is being worked on. Big files always get their own line; a run of small
                // files is throttled by time.
                long now    = sw.ElapsedMilliseconds;
                bool last   = i == toFull.Count - 1;
                bool barDue = last || now - lastBar >= 100;
                bool logDue = compute && (c.Size >= 25L * 1024 * 1024 || now - lastLog >= 600);
                if (barDue || logDue)
                {
                    var sp = new ScanProgress
                    {
                        Phase          = "Full hashing…",
                        FilesProcessed = i,
                        FilesTotal     = total,
                        Fraction       = 0.15 + 0.85 * i / total,
                        CurrentFile    = c.Path
                    };
                    if (logDue)
                    {
                        sp.LogMessage = $"  hashing [{i + 1}/{total}] {Path.GetFileName(c.Path)} " +
                                        $"({DuplicateGroup.FormatBytes(c.Size)})…";
                        lastLog = now;
                    }
                    progress?.Report(sp);
                    lastBar = now;
                }

                if (compute)
                {
                    try { c.Cache.FullHash = ComputeFullHash(c.Path, ct); }
                    catch { c.Cache.FullHash = "?" + Guid.NewGuid().ToString("N"); }
                    fullComputed++;
                }
                else fullCached++;
            }

            progress?.Report(new ScanProgress
            {
                LogMessage = $"  full hash done — {fullComputed} newly hashed, {fullCached} reused from cache."
            });

            // ── Build result groups ─────────────────────────────────────────────
            var groups = toFull
                .GroupBy(c => c.Cache.FullHash)
                .Where(g => g.Count() > 1)
                .Select(g =>
                {
                    var grp = new DuplicateGroup
                    {
                        Hash      = g.Key,
                        SizeBytes = g.First().Size
                    };
                    foreach (var c in g.OrderBy(x => x.FolderOrder)
                                       .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        grp.Files.Add(new DuplicateFile
                        {
                            FullPath    = c.Path,
                            SizeBytes   = c.Size,
                            FolderOrder = c.FolderOrder
                        });
                    }
                    grp.ApplyDefaultKeep();
                    return grp;
                })
                .OrderByDescending(g => g.ReclaimableBytes)
                .ToList();

            progress?.Report(new ScanProgress
            {
                Phase      = "Done",
                Fraction   = 1,
                LogMessage = $"  confirmed {groups.Count} duplicate group(s) after full comparison."
            });
            return groups;
        }

        private static string ComputeQuickHash(string path, long size, CancellationToken ct)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                           ReadBuffer, FileOptions.SequentialScan))
            {
                // Fold the size in so two files that share the same head/tail but differ
                // in length never collide.
                byte[] sizeBytes = BitConverter.GetBytes(size);
                sha.TransformBlock(sizeBytes, 0, sizeBytes.Length, null, 0);

                var buf = new byte[QuickHashChunk];

                int read = ReadUpTo(fs, buf, QuickHashChunk);
                sha.TransformBlock(buf, 0, read, null, 0);

                if (size > QuickHashChunk * 2L)
                {
                    fs.Seek(-QuickHashChunk, SeekOrigin.End);
                    read = ReadUpTo(fs, buf, QuickHashChunk);
                    sha.TransformBlock(buf, 0, read, null, 0);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static string ComputeFullHash(string path, CancellationToken ct)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                           ReadBuffer, FileOptions.SequentialScan))
            {
                var buf = new byte[ReadBuffer];
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, read, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static int ReadUpTo(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
