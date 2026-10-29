using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Kizuna
{
    /// <summary>
    /// HTTP client for the remote anime-tracking backend API.
    ///
    /// All endpoints use form-encoded POST and are automatically authenticated
    /// with the username and API token supplied at construction time.
    /// Every method throws on HTTP or parsing failure so callers can decide
    /// whether to log, retry, or fall back.
    /// </summary>
    internal class BackendApiClient
    {
        private readonly string _baseUrl;
        private readonly string _username;
        private readonly string _apiToken;

        public BackendApiClient(string baseUrl, string username, string apiToken)
        {
            // Normalise the base URL so endpoints can always be appended with simple
            // string concatenation, regardless of whether the user typed a trailing slash.
            _baseUrl = baseUrl.TrimEnd('/') + "/";
            _username = username;
            _apiToken = apiToken;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Returns all files the backend has not yet matched to a rule.</summary>
        public IReadOnlyList<UnrecognizedFile> GetUnrecognizedFiles()
            => FetchFileList<UnrecognizedFile>("unrecognized-files");

        /// <summary>Returns all files matched to a rule and queued for download.</summary>
        public IReadOnlyList<RecognizedFile> GetRecognizedFiles()
            => FetchFileList<RecognizedFile>("recognized-files");

        /// <summary>Returns the current total number of active download rules.</summary>
        public int GetRuleCount()
            => Convert.ToInt32(Post("rule-count", new Dictionary<string, string>()));

        /// <summary>
        /// Tells the backend that <paramref name="entryId"/> has been handed off to
        /// the torrent client and should be removed from the download queue.
        /// </summary>
        public void MarkAsDownloaded(int entryId)
            => Post("mark-downloaded",
                new Dictionary<string, string> { { "entryId", entryId.ToString() } });

        /// <summary>Deletes a scraped entry on the backend (admin accounts only).</summary>
        public void DeleteEntry(int entryId)
            => Post("delete-entry",
                new Dictionary<string, string> { { "entryId", entryId.ToString() } });

        /// <summary>
        /// Notifies the backend that the finished torrent named <paramref name="filename"/>
        /// has been removed from the torrent client.
        /// </summary>
        public void NotifyTorrentRemoved(string filename)
            => Post("unrecognized-files",
                new Dictionary<string, string> { { "name", filename } });

        /// <summary>Returns the franchise name for <paramref name="filename"/>, or null on failure.</summary>
        public string GetFranchiseName(string filename)
            => ParseStringResponse(
                Post("franchise-name", new Dictionary<string, string> { { "filename", filename } }));

        /// <summary>Returns the season display name for <paramref name="filename"/>, or null on failure.</summary>
        public string GetSeasonName(string filename)
            => ParseStringResponse(
                Post("season-name", new Dictionary<string, string> { { "filename", filename } }));

        /// <summary>Returns the season number for <paramref name="filename"/>, or -1 on failure.</summary>
        public int GetSeasonNumber(string filename)
        {
            string raw = ParseStringResponse(
                Post("season-number", new Dictionary<string, string> { { "filename", filename } }));
            return int.TryParse(raw, out int n) ? n : -1;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Shared loader for both file-list endpoints.
        /// The backend wraps every item in a one-element list inside a keyed dictionary,
        /// so we flatten: dict values → lists → first element of each list.
        /// </summary>
        private IReadOnlyList<T> FetchFileList<T>(string endpoint)
        {
            string json = Post(endpoint, new Dictionary<string, string>());
            var response = Deserialize<ApiListResponse<T>>(json);

            return response?.Items?
                .Values
                .Where(list => list?.Count > 0)
                .Select(list => list[0])
                .ToList()
                ?? new List<T>();
        }

        /// <summary>
        /// Sends a form-encoded POST to <c>_baseUrl + endpoint</c>.
        /// Auth credentials are injected into <paramref name="fields"/> automatically.
        /// </summary>
        private string Post(string endpoint, Dictionary<string, string> fields)
        {
            using (var client = new TimeoutWebClient())
            {
                client.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";

                fields["username"] = _username;
                fields["apitoken"] = _apiToken;

                string body = string.Join("&",
                    fields.Select(kv =>
                        $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));

                byte[] bytes = client.UploadData(
                    _baseUrl + endpoint, "POST", Encoding.UTF8.GetBytes(body));

                return Encoding.UTF8.GetString(bytes);
            }
        }

        /// <summary>
        /// WebClient that fails fast instead of hanging, and never reuses a pooled
        /// connection. The latter matters after the machine wakes from sleep: the
        /// keep-alive sockets opened before sleep are dead, and reusing them would
        /// block the (UI-thread) caller until the OS-level TCP timeout — stalling
        /// every polling timer. A fresh connection per request avoids that.
        /// </summary>
        private sealed class TimeoutWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                if (request is HttpWebRequest http)
                {
                    http.Timeout          = 20000; // 20 s
                    http.ReadWriteTimeout = 20000;
                    http.KeepAlive        = false;
                }
                return request;
            }
        }

        private static T Deserialize<T>(string json)
        {
            using (var sr = new StringReader(json))
            using (var jr = new JsonTextReader(sr))
            {
                var serializer = new JsonSerializer { Formatting = Formatting.Indented };
                return jr.Read() ? serializer.Deserialize<T>(jr) : default;
            }
        }

        /// <summary>
        /// Mirrors the JSON envelope the backend uses for file-list responses:
        /// a dictionary where each value is a one-element list containing one file entry.
        /// </summary>
        private class ApiListResponse<T>
        {
            public Dictionary<string, List<T>> Items { get; set; }
            public string[] Errors { get; set; }
        }

        // Strip JSON string quotes from simple string responses like "\"Quanzhi Fashi\""
        // Returns null for empty, whitespace-only, or JSON null responses.
        private static string ParseStringResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            json = json.Trim();
            if (json.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            if (json.StartsWith("\"") && json.EndsWith("\"") && json.Length >= 2)
            {
                string inner = json.Substring(1, json.Length - 2);
                return string.IsNullOrWhiteSpace(inner) ? null : inner;
            }
            return json;
        }
    }
}
