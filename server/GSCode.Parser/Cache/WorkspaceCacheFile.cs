namespace GSCode.Parser.Cache;

/// <summary>
/// Top-level model for the workspace cache file.
/// Contains versioning metadata and per-script cached data keyed by full file path.
/// </summary>
public sealed record WorkspaceCacheFile
{
    /// <summary>
    /// Format version of the cache file structure.
    /// Increment this when making breaking changes to the schema.
    /// Mismatched versions will cause the cache to be discarded.
    /// </summary>
    public required int FormatVersion { get; init; }

    /// <summary>
    /// Server version string (e.g., assembly version or git commit hash).
    /// Can be used for additional validation or diagnostics.
    /// </summary>
    public required string ServerVersion { get; init; }

    /// <summary>
    /// Timestamp when this cache file was last saved.
    /// </summary>
    public required DateTime LastSaved { get; init; }

    /// <summary>
    /// Cached parse results for each script, keyed by full file path (case-insensitive on Windows).
    /// </summary>
    public required Dictionary<string, CachedScriptData> Scripts { get; init; }
}
