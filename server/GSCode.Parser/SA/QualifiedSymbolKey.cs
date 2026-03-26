using GSCode.Parser.Lexical;

namespace GSCode.Parser.SA;

/// <summary>
/// A normalized, case-insensitive key identifying a symbol within a qualifier scope.
/// The qualifier is typically a script namespace or class name (the part before <c>::</c> in GSC),
/// and the symbol name is the function, method, or class identifier.
/// Both components are interned and lowercased for O(1) equality and minimal memory usage.
/// </summary>
public readonly record struct QualifiedSymbolKey(string Qualifier, string SymbolName)
{
    /// <summary>
    /// Creates a <see cref="QualifiedSymbolKey"/> with both components normalized to lowercase
    /// and interned via <see cref="StringPool"/>.
    /// </summary>
    public static QualifiedSymbolKey Normalized(string qualifier, string symbolName)
    {
        return new QualifiedSymbolKey(
            StringPool.Intern(qualifier?.ToLowerInvariant() ?? string.Empty),
            StringPool.Intern(symbolName?.ToLowerInvariant() ?? string.Empty)
        );
    }
}
