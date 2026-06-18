using GSCode.Data.Models;
using GSCode.Parser.Lexical;

namespace GSCode.Parser.SA;

/// <summary>
/// A workspace-wide function symbol as exposed to completions.
/// <see cref="Function"/> carries the full <see cref="ScrFunction"/> when the defining
/// script is loaded; the remaining fields are always populated so completion items can
/// be built even for symbols restored from a cache without live script objects.
/// </summary>
public sealed record WorkspaceFunctionInfo(
    string Namespace,
    string Name,
    string FilePath,
    FunctionParameter[]? Parameters,
    string? Documentation,
    ScrFunction? Function);

/// <summary>
/// Interface for providing symbol location lookups from a global registry.
/// This allows DefinitionsTable to query a workspace-wide symbol database
/// without creating a circular dependency between Parser and NET projects.
/// </summary>
public interface ISymbolLocationProvider
{
    (string FilePath, TokenRange Range)? FindFunctionLocation(string? ns, string name);
    (string FilePath, TokenRange Range)? FindClassLocation(string? ns, string name);
    (string FilePath, TokenRange Range)? FindFunctionLocationAnyNamespace(string name);
    (string FilePath, TokenRange Range)? FindClassLocationAnyNamespace(string name);
    FunctionParameter[]? GetFunctionParameters(string ns, string name);
    string[]? GetFunctionFlags(string ns, string name);
    string? GetFunctionDoc(string ns, string name);
    ScrFunction? GetFunction(string ns, string name);

    /// <summary>
    /// Returns true if any function defined in a file whose path ends with
    /// <paramref name="filePathSuffix"/> (case-insensitive) carries the given flag.
    /// Used by the unused-#using diagnostic to keep entry-point files alive.
    /// </summary>
    bool AnyFunctionInFileHasFlag(string filePathSuffix, string flag);

    /// <summary>
    /// Returns all namespaces known across the workspace and game script roots for the
    /// given language ("gsc" or "csc"), regardless of whether they are #using'd anywhere.
    /// </summary>
    IReadOnlyCollection<string> GetAllNamespaces(string languageId);

    /// <summary>
    /// Returns all functions exported under the given namespace across every known script
    /// of the given language ("gsc" or "csc"). A namespace may span multiple files.
    /// </summary>
    IReadOnlyList<WorkspaceFunctionInfo> GetFunctionsInNamespace(string ns, string languageId);
}
