namespace GSCode.Data;

/// <summary>
/// Identifies the scripting language of a GSCode script file.
/// </summary>
public enum ScriptLanguage
{
    /// <summary>Game Script Code — uses the .gsc file extension.</summary>
    Gsc,

    /// <summary>Client Script Code — uses the .csc file extension.</summary>
    Csc,
}

/// <summary>
/// Conversion helpers between <see cref="ScriptLanguage"/> and the string identifiers
/// used in JSON serialization, language-server protocol messages, and file extensions.
/// </summary>
public static class ScriptLanguageExtensions
{
    /// <summary>Returns the lowercase string identifier ("gsc" or "csc").</summary>
    public static string ToLanguageId(this ScriptLanguage language) => language switch
    {
        ScriptLanguage.Gsc => "gsc",
        ScriptLanguage.Csc => "csc",
        _ => "gsc",
    };

    /// <summary>Returns the file extension including the leading dot (".gsc" or ".csc").</summary>
    public static string ToExtension(this ScriptLanguage language) => language switch
    {
        ScriptLanguage.Gsc => ".gsc",
        ScriptLanguage.Csc => ".csc",
        _ => ".gsc",
    };

    /// <summary>
    /// Resolves a <see cref="ScriptLanguage"/> from a file extension (with or without leading dot).
    /// Defaults to <see cref="ScriptLanguage.Gsc"/> for unrecognised extensions.
    /// </summary>
    public static ScriptLanguage FromExtension(string extension) =>
        extension.TrimStart('.').Equals("csc", StringComparison.OrdinalIgnoreCase)
            ? ScriptLanguage.Csc
            : ScriptLanguage.Gsc;

    /// <summary>
    /// Resolves a <see cref="ScriptLanguage"/> from a raw string identifier ("gsc"/"csc").
    /// Defaults to <see cref="ScriptLanguage.Gsc"/> for unrecognised values.
    /// </summary>
    public static ScriptLanguage FromString(string? languageId) =>
        languageId?.Equals("csc", StringComparison.OrdinalIgnoreCase) is true
            ? ScriptLanguage.Csc
            : ScriptLanguage.Gsc;
}
