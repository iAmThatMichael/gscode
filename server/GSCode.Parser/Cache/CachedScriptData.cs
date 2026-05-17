using GSCode.Data.Models;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Cache;

/// <summary>
/// Serializable representation of a single LSP diagnostic for cache persistence.
/// </summary>
public sealed record CachedDiagnostic(
    int StartLine,
    int StartChar,
    int EndLine,
    int EndChar,
    DiagnosticSeverity? Severity,
    string? Code,
    string Message,
    string? Source
);

/// <summary>
/// Serializable DTO representing cached parse and analysis results for a single script.
/// Contains only the data needed to restore a Script's state without re-parsing.
/// </summary>
public sealed record CachedScriptData
{
    /// <summary>Hash of the file content when this cache entry was created.</summary>
    public required int ContentHash { get; init; }

    /// <summary>Language ID (gsc/csc).</summary>
    public required string LanguageId { get; init; }

    /// <summary>Timestamp when this entry was cached.</summary>
    public required DateTime CachedAt { get; init; }

    /// <summary>The script's namespace (typically the filename without extension).</summary>
    public required string CurrentNamespace { get; init; }

    /// <summary>Functions exported by this script.</summary>
    public required List<ScrFunction> ExportedFunctions { get; init; }

    /// <summary>Classes exported by this script.</summary>
    public required List<ScrClass> ExportedClasses { get; init; }

    /// <summary>File paths this script depends on (via #include or using).</summary>
    public required List<string> Dependencies { get; init; }

    /// <summary>Function locations: qualified key -> (file path, range).</summary>
    public required Dictionary<QualifiedSymbolKey, CachedSymbolLocation> FunctionLocations { get; init; }

    /// <summary>Class locations: qualified key -> (file path, range).</summary>
    public required Dictionary<QualifiedSymbolKey, CachedSymbolLocation> ClassLocations { get; init; }

    /// <summary>Function parameters: qualified key -> parameter names.</summary>
    public required Dictionary<QualifiedSymbolKey, string[]> FunctionParameters { get; init; }

    /// <summary>Function flags: qualified key -> flags.</summary>
    public required Dictionary<QualifiedSymbolKey, string[]> FunctionFlags { get; init; }

    /// <summary>Function documentation: qualified key -> doc comment.</summary>
    public required Dictionary<QualifiedSymbolKey, string?> FunctionDocs { get; init; }

    /// <summary>Macro source paths discovered during preprocessing: name -> source file path (null for macros local to the script).</summary>
    public required Dictionary<string, string?> MacroDefinitions { get; init; }

    /// <summary>Diagnostics produced during parse and analysis, to be re-emitted on cache restore.</summary>
    public required List<CachedDiagnostic> Diagnostics { get; init; }

    /// <summary>
    /// Content hashes of each dependency at the time this entry was cached.
    /// Used at restore time to detect if any dependency changed on disk between runs.
    /// Null for cache entries written before this field was introduced (treated as no dep-hash data → skip phase-1 check).
    /// </summary>
    public Dictionary<string, int>? DependencyHashes { get; init; }
}

/// <summary>
/// Serializable version of (string FilePath, TokenRange Range).
/// </summary>
public sealed record CachedSymbolLocation(string FilePath, TokenRange Range);
