using GSCode.Data.Models;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Cache;

using SaSymbolKind = GSCode.Parser.SA.SymbolKind;

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
/// Serializable representation of one symbol reference range.
/// </summary>
public sealed record CachedReference(
    SaSymbolKind Kind,
    string Namespace,
    string Name,
    string? ClassName,
    string? ScopeId,
    int StartLine,
    int StartChar,
    int EndLine,
    int EndChar
);

/// <summary>
/// Serializable representation of a global-object field access.
/// </summary>
public sealed record CachedGlobalFieldAccess(string OwnerName, string FieldName);

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

    /// <summary>Content hashes for dependencies that affect this script's diagnostics.</summary>
    public required Dictionary<string, int> DependencyContentHashes { get; init; }

    /// <summary>Function locations: qualified key -> (file path, range).</summary>
    public required Dictionary<QualifiedSymbolKey, CachedSymbolLocation> FunctionLocations { get; init; }

    /// <summary>Class locations: qualified key -> (file path, range).</summary>
    public required Dictionary<QualifiedSymbolKey, CachedSymbolLocation> ClassLocations { get; init; }

    /// <summary>Function definitions: qualified key -> complete function metadata (parameters, flags, doc, location).</summary>
    public required Dictionary<QualifiedSymbolKey, CompleteFunctionDefinition> FunctionDefinitions { get; init; }

    /// <summary>Class definitions: qualified key -> complete class metadata (members, doc, location).</summary>
    public required Dictionary<QualifiedSymbolKey, CompleteClassDefinition> ClassDefinitions { get; init; }

    /// <summary>Macro source paths discovered during preprocessing: name -> source file path (null for macros local to the script).</summary>
    public required Dictionary<string, string?> MacroDefinitions { get; init; }

    /// <summary>Diagnostics produced during parse and analysis, to be re-emitted on cache restore.</summary>
    public required List<CachedDiagnostic> Diagnostics { get; init; }

    /// <summary>Reference index entries used by workspace/document references.</summary>
    public required List<CachedReference> References { get; init; }

    /// <summary>Global-object field accesses used by cross-file dot completions.</summary>
    public required List<CachedGlobalFieldAccess> GlobalFieldAccesses { get; init; }
}

/// <summary>
/// Serializable version of (string FilePath, TokenRange Range, int BodyEndLine).
/// BodyEndLine defaults to 0 for backward-compatible deserialization of older cache entries.
/// </summary>
public sealed record CachedSymbolLocation(string FilePath, TokenRange Range, int BodyEndLine = 0);
