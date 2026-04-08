using System.Collections.Concurrent;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

// NOTE: This class uses QualifiedSymbolKey as its dictionary key — the same
// normalized, interned key struct used by DefinitionsTable — so that symbol
// lookups are consistent across the per-script and workspace-wide layers.

namespace GSCode.NET.LSP;

/// <summary>
/// Represents a canonical symbol definition stored in the global registry.
/// This is the single source of truth for symbol metadata across all scripts.
/// </summary>
public sealed record SymbolDefinition(
    string Namespace,
    string Name,
    ExportedSymbolType Type,
    string FilePath,
    TokenRange Range,
    string[]? Parameters = null,
    string[]? Flags = null,
    string? Documentation = null,
    IExportedSymbol? Symbol = null
);

/// <summary>
/// A workspace-wide symbol database that provides O(1) lookup for function and class definitions.
/// This eliminates per-file duplication of symbol metadata and enables efficient cross-file resolution.
/// Implements ISymbolLocationProvider to allow DefinitionsTable to query the registry.
/// </summary>
public sealed class GlobalSymbolRegistry : ISymbolLocationProvider
{
    // Canonical symbol definitions indexed by QualifiedSymbolKey - single source of truth
    private readonly ConcurrentDictionary<QualifiedSymbolKey, SymbolDefinition> _symbols = new();

    // Secondary index: file path -> set of symbol keys defined in that file
    // Enables efficient cleanup when a file is removed or reparsed
    // (ConcurrentDictionary<K, byte> is used as a concurrent hash-set; the byte value is unused)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<QualifiedSymbolKey, byte>> _fileIndex = new(StringComparer.OrdinalIgnoreCase);

    // Index for fast lookup by name only (ignoring namespace)
    // (ConcurrentDictionary<K, byte> is used as a concurrent hash-set; the byte value is unused)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<QualifiedSymbolKey, byte>> _nameIndex = new(StringComparer.OrdinalIgnoreCase);

    // Reader-writer lock for operations that need atomicity across multiple dictionaries
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>
    /// Finds a symbol by namespace and name. If namespace is provided, searches that namespace first.
    /// Falls back to searching all namespaces if not found.
    /// </summary>
    public SymbolDefinition? FindSymbol(string? ns, string name)
    {
        _lock.EnterReadLock();
        try
        {
            // Try exact namespace match first
            if (ns is not null)
            {
                var key = QualifiedSymbolKey.Normalized(ns, name);
                if (_symbols.TryGetValue(key, out var def))
                    return def;

                // An explicit namespace was given but no exact match exists.
                // Do NOT fall back to a name-only scan: that would silently
                // resolve ns::func to an unrelated function that merely shares
                // the same name in a different namespace, causing GoTo to jump
                // to the wrong file.
                return null;
            }

            // Unqualified reference — search by name only across all namespaces.
            return FindSymbolByNameOnlyCore(name);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Finds a symbol by name only, searching across all namespaces.
    /// </summary>
    private SymbolDefinition? FindSymbolByNameOnly(string name)
    {
        _lock.EnterReadLock();
        try
        {
            return FindSymbolByNameOnlyCore(name);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Internal name-only lookup. Caller must already hold the read lock.
    /// </summary>
    private SymbolDefinition? FindSymbolByNameOnlyCore(string name)
    {
        var normalizedName = name?.ToLowerInvariant() ?? string.Empty;
        if (_nameIndex.TryGetValue(normalizedName, out var keys))
        {
            foreach (var key in keys.Keys)
            {
                if (_symbols.TryGetValue(key, out var def))
                    return def;
            }
        }
        return null;
    }

    /// <summary>
    /// Removes all symbols defined in a specific file.
    /// Call this before reparsing a file to ensure stale symbols are removed.
    /// </summary>
    public void RemoveSymbolsFromFile(string filePath)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_fileIndex.TryRemove(filePath, out var keys))
                return;

            foreach (var key in keys.Keys)
            {
                if (_symbols.TryRemove(key, out var removed))
                {
                    // Remove from name index
                    var normalizedName = removed.Name.ToLowerInvariant();
                    if (_nameIndex.TryGetValue(normalizedName, out var nameKeys))
                    {
                        nameKeys.TryRemove(key, out _);
                        if (nameKeys.IsEmpty)
                            _nameIndex.TryRemove(normalizedName, out _);
                    }
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Updates symbols for a file and returns whether any symbols actually changed.
    /// This is more efficient than RemoveSymbolsFromFile + AddOrUpdateSymbol for incremental updates.
    /// </summary>
    /// <param name="filePath">The file path being updated.</param>
    /// <param name="newSymbols">The new set of symbols for the file.</param>
    /// <returns>True if symbols changed (added, removed, or modified), false if identical.</returns>
    public bool UpdateSymbolsForFile(string filePath, IEnumerable<SymbolDefinition> newSymbols)
    {
        _lock.EnterWriteLock();
        try
        {
            var newSymbolsList = newSymbols.ToList();
            var newKeys = new HashSet<QualifiedSymbolKey>();
            bool changed = false;

            // Get existing symbols for this file
            _fileIndex.TryGetValue(filePath, out var existingKeys);
            var existingKeySet = existingKeys?.Keys.ToHashSet() ?? new HashSet<QualifiedSymbolKey>();

            // Add/update new symbols
            foreach (var def in newSymbolsList)
            {
                var key = QualifiedSymbolKey.Normalized(def.Namespace, def.Name);
                newKeys.Add(key);

                // Check if symbol exists and is identical
                if (_symbols.TryGetValue(key, out var existing))
                {
                    if (!SymbolsEqual(existing, def))
                    {
                        changed = true;
                        _symbols[key] = def;
                    }
                }
                else
                {
                    // New symbol
                    changed = true;
                    _symbols[key] = def;

                    // Update name index
                    var nameSymbols = _nameIndex.GetOrAdd(StringPool.Intern(def.Name.ToLowerInvariant()),
                        _ => new ConcurrentDictionary<QualifiedSymbolKey, byte>());
                    nameSymbols[key] = 0;
                }
            }

            // Remove symbols that no longer exist in this file
            var removedKeys = existingKeySet.Except(newKeys).ToList();
            foreach (var key in removedKeys)
            {
                changed = true;
                if (_symbols.TryRemove(key, out var removed))
                {
                    // Remove from name index
                    var normalizedName = removed.Name.ToLowerInvariant();
                    if (_nameIndex.TryGetValue(normalizedName, out var nameKeys))
                    {
                        nameKeys.TryRemove(key, out _);
                        if (nameKeys.IsEmpty)
                            _nameIndex.TryRemove(normalizedName, out _);
                    }
                }
            }

            // Update file index
            if (newKeys.Count > 0)
            {
                var fileSymbols = _fileIndex.GetOrAdd(filePath,
                    _ => new ConcurrentDictionary<QualifiedSymbolKey, byte>());
                fileSymbols.Clear();
                foreach (var key in newKeys)
                    fileSymbols[key] = 0;
            }
            else
            {
                _fileIndex.TryRemove(filePath, out _);
            }

            return changed;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Compares two symbol definitions for equality (ignoring IExportedSymbol reference).
    /// </summary>
    private static bool SymbolsEqual(SymbolDefinition? a, SymbolDefinition? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return a.Namespace == b.Namespace &&
               a.Name == b.Name &&
               a.Type == b.Type &&
               string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase) &&
               a.Range.Equals(b.Range) &&
               SequenceEqual(a.Parameters, b.Parameters) &&
               SequenceEqual(a.Flags, b.Flags) &&
               a.Documentation == b.Documentation;
    }

    private static bool SequenceEqual(string[]? a, string[]? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Gets counts of symbols by type (functions and classes).
    /// </summary>
    public (int Functions, int Classes) GetCountsByType()
    {
        _lock.EnterReadLock();
        try
        {
            int functionCount = 0;
            int classCount = 0;

            foreach (var symbol in _symbols.Values)
            {
                if (symbol.Type == ExportedSymbolType.Function)
                    functionCount++;
                else if (symbol.Type == ExportedSymbolType.Class)
                    classCount++;
            }

            return (functionCount, classCount);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    #region ISymbolLocationProvider Implementation

    /// <inheritdoc/>
    (string FilePath, TokenRange Range)? ISymbolLocationProvider.FindFunctionLocation(string? ns, string name)
    {
        var symbol = ns is not null ? FindSymbol(ns, name) : FindSymbolByNameOnly(name);
        if (symbol is not null && symbol.Type == ExportedSymbolType.Function)
            return (symbol.FilePath, symbol.Range);
        return null;
    }

    /// <inheritdoc/>
    (string FilePath, TokenRange Range)? ISymbolLocationProvider.FindClassLocation(string? ns, string name)
    {
        var symbol = ns is not null ? FindSymbol(ns, name) : FindSymbolByNameOnly(name);
        if (symbol is not null && symbol.Type == ExportedSymbolType.Class)
            return (symbol.FilePath, symbol.Range);
        return null;
    }

    /// <inheritdoc/>
    (string FilePath, TokenRange Range)? ISymbolLocationProvider.FindFunctionLocationAnyNamespace(string name)
    {
        var symbol = FindSymbolByNameOnly(name);
        if (symbol is not null && symbol.Type == ExportedSymbolType.Function)
            return (symbol.FilePath, symbol.Range);
        return null;
    }

    /// <inheritdoc/>
    (string FilePath, TokenRange Range)? ISymbolLocationProvider.FindClassLocationAnyNamespace(string name)
    {
        var symbol = FindSymbolByNameOnly(name);
        if (symbol is not null && symbol.Type == ExportedSymbolType.Class)
            return (symbol.FilePath, symbol.Range);
        return null;
    }

    /// <inheritdoc/>
    string[]? ISymbolLocationProvider.GetFunctionParameters(string ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Function ? symbol.Parameters : null;
    }

    /// <inheritdoc/>
    string[]? ISymbolLocationProvider.GetFunctionFlags(string ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Function ? symbol.Flags : null;
    }

    /// <inheritdoc/>
    string? ISymbolLocationProvider.GetFunctionDoc(string ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Function ? symbol.Documentation : null;
    }

    /// <inheritdoc/>
    ScrFunction? ISymbolLocationProvider.GetFunction(string ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Function ? symbol.Symbol as ScrFunction : null;
    }

    #endregion

    /// <summary>
    /// Returns all distinct file paths that define at least one symbol in the given namespace.
    /// Used by the code action handler to suggest <c>#using</c> directives when a namespace is unknown.
    /// </summary>
    /// <param name="namespaceName">The namespace to search for (case-insensitive).</param>
    /// <returns>A list of file paths, or an empty list if no files define this namespace.</returns>
    public List<string> FindFilesForNamespace(string namespaceName)
    {
        var normalizedNs = namespaceName?.ToLowerInvariant() ?? string.Empty;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _lock.EnterReadLock();
        try
        {
            foreach (var (key, def) in _symbols)
            {
                if (string.Equals(key.Qualifier, normalizedNs, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(def.FilePath);
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        return result.ToList();
    }
}
