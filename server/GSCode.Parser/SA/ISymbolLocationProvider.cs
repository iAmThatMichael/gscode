using GSCode.Data.Models;
using GSCode.Parser.Lexical;

namespace GSCode.Parser.SA;

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
    string[]? GetFunctionParameters(string ns, string name);
    string[]? GetFunctionFlags(string ns, string name);
    string? GetFunctionDoc(string ns, string name);
    ScrFunction? GetFunction(string ns, string name);
}
