using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Transmission.API.RPC;
using Transmission.API.RPC.Entity;

namespace Kizuna
{
    /// <summary>Snapshot of a single Transmission torrent's download state.</summary>
    internal sealed class TorrentProgress
    {
        public int    Id           { get; set; }
        public string Name         { get; set; }
        public double PercentDone  { get; set; }
        public long   RateDownload { get; set; }
        public int    Eta          { get; set; }
        public int    Status       { get; set; }
    }

    /// <summary>
    /// Encapsulates all interactions with the local Transmission daemon via its RPC API.
    ///
    /// Each public method opens a fresh session. <see cref="Client.GetSessionInformation"/>
    /// must be called first on every session so the library can negotiate the
    /// X-Transmission-Session-Id CSRF token that Transmission requires.
    ///
    /// Every method throws on RPC failure so callers can decide how to handle errors.
    /// </summary>
    internal class TransmissionService
    {
        private readonly string _rpcUrl;

        public TransmissionService(string rpcUrl = "http://127.0.0.1:9091/transmission/rpc")
        {
            _rpcUrl = rpcUrl;
        }

        // ── Session helper ────────────────────────────────────────────────────

        private Client OpenSession()
        {
            var client = new Client(_rpcUrl);
            client.GetSessionInformation();
            return client;
        }

        // ── Public operations ─────────────────────────────────────────────────

        /// <summary>
        /// Removes all torrents that are 100% complete, seeding (status 6),
        /// have no remaining ETA (-1), and have no error.
        /// Returns the names of successfully removed torrents so the caller
        /// can notify the backend.
        /// </summary>
        public IReadOnlyList<string> RemoveFinishedTorrents()
        {
            var client = OpenSession();
            var removedNames = new List<string>();

            foreach (var torrent in client.TorrentGet(TorrentFields.ALL_FIELDS).Torrents)
            {
                bool isFinished = torrent.PercentDone == 1
                               && torrent.Status == 6
                               && torrent.ETA == -1
                               && torrent.Error == 0;

                if (!isFinished) continue;

                client.TorrentRemove(new int[] { torrent.ID });

                // Re-query to confirm the torrent is actually gone before reporting success.
                bool isGone = client.TorrentGet(TorrentFields.ALL_FIELDS)
                                    .Torrents.All(t => t.ID != torrent.ID);
                if (isGone)
                    removedNames.Add(torrent.Name);
            }

            return removedNames;
        }

        /// <summary>Removes a single torrent by ID. Downloaded data is kept on disk.</summary>
        public void RemoveTorrent(int id)
        {
            var client = OpenSession();
            client.TorrentRemove(new int[] { id });
        }

        /// <summary>
        /// Adds a torrent to Transmission, starts it immediately, and returns its
        /// assigned ID. Returns 0 if the add produced no valid torrent.
        ///
        /// Download strategy:
        ///   1. If <see cref="RecognizedFile.Magnet"/> ends in ".torrent", the file is
        ///      downloaded to <paramref name="torrentFileDirectory"/> and added from disk.
        ///   2. Otherwise the magnet URI or URL is passed directly.
        ///   3. On failure, a minimal magnet link built from
        ///      <see cref="RecognizedFile.MagnetHash"/> is tried as a last resort.
        /// </summary>
        public int AddAndStartTorrent(RecognizedFile file, string torrentFileDirectory)
        {
            Client client;
            try
            {
                client = OpenSession();
            }
            catch (System.Exception ex)
            {
                // A 401 here usually means Transmission's RPC requires authentication,
                // which this client does not currently send.
                throw new System.Exception(
                    $"Could not open a Transmission RPC session at '{_rpcUrl}'. " +
                    $"Underlying error: {ex.Message}", ex);
            }

            var torrent = new NewTorrent
            {
                Filename = file.Magnet,
                DownloadDirectory = file.Path,
                Paused = false
            };

            try
            {
                if (file.Magnet.EndsWith(".torrent"))
                    torrent.Filename = DownloadTorrentFile(file.Magnet, torrentFileDirectory);

                return AddAndStart(client, torrent);
            }
            catch (System.Exception primaryEx)
            {
                // Primary source failed; fall back to a bare magnet link from the info-hash.
                string fallbackMagnet = $"magnet:?xt=urn:btih:{file.MagnetHash}";
                torrent.Filename = fallbackMagnet;
                try
                {
                    return AddAndStart(client, torrent);
                }
                catch (System.Exception fallbackEx)
                {
                    throw new System.Exception(
                        $"Failed to add torrent to Transmission at '{_rpcUrl}'. " +
                        $"Source: '{file.Magnet}', download dir: '{file.Path}', " +
                        $"fallback magnet: '{fallbackMagnet}'. " +
                        $"Primary error: {primaryEx.Message} | Fallback error: {fallbackEx.Message}",
                        fallbackEx);
                }
            }
        }

        /// <summary>
        /// Returns progress snapshots for every torrent currently in Transmission,
        /// regardless of download state. Used by the UI to update in-progress rows
        /// and detect when a download finishes.
        /// </summary>
        public IReadOnlyList<TorrentProgress> GetActiveTorrents()
        {
            var client = OpenSession();
            var result = new List<TorrentProgress>();
            foreach (var t in client.TorrentGet(TorrentFields.ALL_FIELDS).Torrents)
            {
                result.Add(new TorrentProgress
                {
                    Id           = t.ID,
                    Name         = t.Name,
                    PercentDone  = t.PercentDone,
                    RateDownload = t.RateDownload,
                    Eta          = t.ETA,
                    Status       = t.Status
                });
            }
            return result;
        }

        /// <summary>Resumes all paused torrents.</summary>
        public void StartAll()
        {
            var client = OpenSession();
            foreach (var t in client.TorrentGet(TorrentFields.ALL_FIELDS).Torrents)
                client.TorrentStart(new int[] { t.ID });
        }

        /// <summary>Pauses all active torrents.</summary>
        public void StopAll()
        {
            var client = OpenSession();
            foreach (var t in client.TorrentGet(TorrentFields.ALL_FIELDS).Torrents)
                client.TorrentStop(new int[] { t.ID });
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Adds <paramref name="torrent"/> to the open <paramref name="client"/> session,
        /// starts it, and returns its ID. Returns 0 if Transmission returned no valid info.
        /// </summary>
        private static int AddAndStart(Client client, NewTorrent torrent)
        {
            NewTorrentInfo added = client.TorrentAdd(torrent);
            if (added?.ID > 0)
                client.TorrentStart(new int[] { added.ID });
            return added?.ID ?? 0;
        }

        /// <summary>
        /// Downloads a .torrent file from <paramref name="url"/> to
        /// <paramref name="directory"/> and returns the resulting local path.
        /// </summary>
        private static string DownloadTorrentFile(string url, string directory)
        {
            string localPath = Path.Combine(directory, Path.GetFileName(url));
            try
            {
                using (var webClient = new WebClient())
                    webClient.DownloadFile(url, localPath);
            }
            catch (WebException ex)
            {
                throw new WebException(
                    $"Downloading .torrent file from '{url}' to '{localPath}' failed: {ex.Message}", ex);
            }
            return localPath;
        }
    }
}
