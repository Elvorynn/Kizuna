namespace Kizuna
{
    /// <summary>
    /// Represents a single file entry that the backend has not yet matched to any
    /// download rule. Bound directly to the left DataGrid in the main window.
    /// Property names must match the JSON keys returned by the API.
    /// </summary>
    public class UnrecognizedFile
    {
        /// <summary>Backend-assigned identifier for this file entry.</summary>
        public string Id { get; set; }

        /// <summary>Raw filename as reported by the backend.</summary>
        public string Name { get; set; }
    }
}
