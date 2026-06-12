namespace GSCode.Parser.Configuration;

/// <summary>
/// Indexing mode for workspace files.
/// </summary>
public enum IndexingMode
{
    /// <summary>
    /// Workspace indexing is disabled.
    /// No automatic indexing of workspace files on startup.
    /// </summary>
    Off,

    /// <summary>
    /// Parse and perform signature analysis only (fast).
    /// Provides symbol information, go-to-definition, and completions.
    /// No diagnostics for unopened files.
    /// </summary>
    Partial,

    /// <summary>
    /// Parse and perform full semantic analysis (slower).
    /// Provides diagnostics, data flow analysis, and all language features.
    /// </summary>
    Full
}

/// <summary>
/// Global configuration for completion features.
/// Read-only static accessor that delegates to the DI-managed
/// <see cref="CompletionOptions.Current"/> instance, so parser-layer code
/// that cannot use DI still reads live values.
/// For writes, inject <see cref="CompletionOptions"/> directly.
/// </summary>
public static class CompletionConfiguration
{
    /// <summary>
    /// Optional user-configured custom path to the "raw" folder for path completions.
    /// This overrides automatic detection via TA_GAME_PATH.
    /// </summary>
    public static string? CustomRawPath => CompletionOptions.Current.CustomRawPath;

    /// <summary>
    /// Whether to allow writing/saving files to raw folders (default/custom).
    /// When false (default), attempts to save to raw folders will be blocked.
    /// This helps prevent accidental modifications to vanilla game files.
    /// </summary>
    public static bool AllowRawFolderWrites => CompletionOptions.Current.AllowRawFolderWrites;

    /// <summary>
    /// Indexing mode for workspace files during initial indexing.
    /// - Off: Workspace indexing is disabled.
    /// - Partial: Fast indexing with signature analysis only (no diagnostics for unopened files).
    /// - Full: Complete semantic analysis with diagnostics (slower).
    /// Default is Off for better startup performance. Users can enable Partial or Full as needed.
    /// </summary>
    public static IndexingMode WorkspaceIndexingMode => CompletionOptions.Current.WorkspaceIndexingMode;

    /// <summary>
    /// Whether the lightweight signature-only index of all game scripts runs at startup.
    /// </summary>
    public static bool IndexGameScripts => CompletionOptions.Current.IndexGameScripts;

    /// <summary>
    /// When to warn about saving files inside a protected raw folder.
    /// </summary>
    public static RawFileWarningMode RawFileWarningMode => CompletionOptions.Current.RawFileWarningMode;
}
