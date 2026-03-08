namespace GSCode.Parser.Configuration;

/// <summary>
/// Global configuration for completion features.
/// </summary>
public static class CompletionConfiguration
{
    /// <summary>
    /// Optional user-configured custom path to the "raw" folder for path completions.
    /// This overrides automatic detection via TA_GAME_PATH.
    /// Set this from the LSP server's configuration handler.
    /// </summary>
    public static string? CustomRawPath { get; set; }

    /// <summary>
    /// Whether to allow writing/saving files to raw folders (default/custom).
    /// When false (default), attempts to save to raw folders will be blocked.
    /// This helps prevent accidental modifications to vanilla game files.
    /// </summary>
    public static bool AllowRawFolderWrites { get; set; } = false;
}
