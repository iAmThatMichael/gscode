namespace GSCode.Parser.Configuration;

/// <summary>
/// Workspace-level options configured by the LSP client at startup and updated at
/// runtime via workspace/didChangeConfiguration.
/// <para>
/// In the language server, this is registered as a DI singleton via
/// <c>services.AddSingleton&lt;CompletionOptions&gt;()</c>.  Parser-layer code that
/// cannot use DI reads the same instance through <see cref="Current"/>.
/// </para>
/// </summary>
public sealed class CompletionOptions
{
    private static CompletionOptions _current = new();

    /// <summary>
    /// Gets the live options instance. Readable from any layer once
    /// <see cref="SetCurrent"/> has been called by the language server during
    /// its OnInitialize callback.
    /// </summary>
    public static CompletionOptions Current => _current;

    /// <summary>
    /// Replaces the live instance with the DI-managed singleton so the parser
    /// layer and the server layer share the exact same object.
    /// Call this once from the LSP server OnInitialize callback.
    /// </summary>
    public static void SetCurrent(CompletionOptions options) => _current = options;

    /// <summary>
    /// Optional user-configured custom path to the "raw" folder for path completions.
    /// Overrides automatic detection via TA_GAME_PATH.
    /// </summary>
    public string? CustomRawPath { get; set; }

    /// <summary>
    /// Whether to allow writing/saving files to raw folders (default/custom).
    /// When false (default), saves to raw folders are blocked.
    /// </summary>
    public bool AllowRawFolderWrites { get; set; } = false;

    /// <summary>
    /// Indexing mode for workspace files during initial indexing.
    /// Default is Off for better startup performance.
    /// </summary>
    public IndexingMode WorkspaceIndexingMode { get; set; } = IndexingMode.Off;

    /// <summary>
    /// Whether to run the lightweight signature-only index of all game scripts at startup
    /// (workspace folders plus the shared raw folder). This is what makes every namespace
    /// known to completions and #using quick fixes, independent of WorkspaceIndexingMode.
    /// </summary>
    public bool IndexGameScripts { get; set; } = true;

    /// <summary>
    /// Controls when a warning is shown for saving files inside a protected raw folder.
    /// Stock (default) warns only for scripts that shipped with the game's mod tools,
    /// so user-owned shared scripts kept in share/raw stay quiet.
    /// </summary>
    public RawFileWarningMode RawFileWarningMode { get; set; } = RawFileWarningMode.Stock;
}

/// <summary>
/// When to warn about saving a file inside a protected raw folder.
/// </summary>
public enum RawFileWarningMode
{
    /// <summary>Never warn.</summary>
    Off,

    /// <summary>Warn only when the file is a stock script shipped with the mod tools.</summary>
    Stock,

    /// <summary>Warn for any file inside a protected raw folder.</summary>
    All
}