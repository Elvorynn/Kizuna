using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Kizuna
{
    public class LibraryRule
    {
        public string Id   { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Rule";

        public string SourcePath      { get; set; } = "";
        public bool   MatchSubfolders { get; set; } = true;

        public string TargetBasePath        { get; set; } = "";
        public bool   CreateFranchiseFolder { get; set; } = true;

        // Stored as "CreateSeasonFolder" in JSON for backward compat with existing saved rules.
        // When true: season subfolder is created only once a second season is detected in the
        // franchise folder (smart mode). A _kizuna.info sentinel tracks the current season.
        [JsonProperty("CreateSeasonFolder")]
        public bool CreateSeasonFolderIfMultiple { get; set; } = true;

        public bool UseCleanFilename { get; set; } = true;

        public string MatchModeDisplay => MatchSubfolders ? "incl. subfolders" : "exact match";

        public string OptionsDisplay
        {
            get
            {
                var parts = new List<string>();
                if (CreateFranchiseFolder)         parts.Add("Franchise");
                if (CreateSeasonFolderIfMultiple)  parts.Add("Season (smart)");
                if (UseCleanFilename)              parts.Add("Clean");
                return parts.Count > 0 ? string.Join(", ", parts) : "—";
            }
        }
    }
}
