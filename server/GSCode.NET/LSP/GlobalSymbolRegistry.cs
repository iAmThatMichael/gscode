using System.Collections.Concurrent;
using GSCode.Data.Models;
using Serilog;
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
    /// Returns the source priority of a file path for symbol precedence.
    /// Files under TA_TOOLS_PATH/share/raw are shared-raw (priority 0);
    /// all other files (workspace, mod) are priority 1 and always win.
    /// </summary>
    private static int GetSourcePriority(string filePath)
    {
        string? toolsPath = Environment.GetEnvironmentVariable("TA_TOOLS_PATH");
        if (!string.IsNullOrEmpty(toolsPath))
        {
            string sharedRaw = Path.Combine(toolsPath, "share", "raw");
            if (filePath.StartsWith(sharedRaw, StringComparison.OrdinalIgnoreCase))
                return 0;
        }
        return 1;
    }

    /// <summary>
    /// Internal name-only lookup. Caller must already hold the read lock.
    /// When multiple namespaces define the same name, returns the definition from the
    /// highest-priority source (mod/local > shared raw). Logs ambiguity when two
    /// candidates share the same priority level.
    /// </summary>
    private SymbolDefinition? FindSymbolByNameOnlyCore(string name)
    {
        var normalizedName = name?.ToLowerInvariant() ?? string.Empty;
        if (!_nameIndex.TryGetValue(normalizedName, out var keys))
            return null;

        SymbolDefinition? best = null;
        int bestPriority = -1;
        int candidateCount = 0;

        foreach (var key in keys.Keys)
        {
            if (!_symbols.TryGetValue(key, out var def))
                continue;

            candidateCount++;
            int priority = GetSourcePriority(def.FilePath);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                best = def;
            }
        }

        if (candidateCount > 1 && best is not null)
        {
            Log.Debug("SYMBOL_AMBIGUOUS: unqualified '{Name}' resolved to {Namespace}::{Symbol} (priority={Priority}, {Count} candidates)",
                name, best.Namespace, best.Name, bestPriority, candidateCount);
        }

        return best;
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
                // Only remove the canonical entry if this file is the current owner.
                // A higher-priority file (e.g. a mod override) may have taken ownership;
                // removing that entry here would silently discard the winning definition.
                if (!(_symbols.TryGetValue(key, out var owner) &&
                      string.Equals(owner.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                      _symbols.TryRemove(key, out var removed)))
                    continue;

                var normalizedName = removed.Name.ToLowerInvariant();
                if (_nameIndex.TryGetValue(normalizedName, out var nameKeys))
                {
                    nameKeys.TryRemove(key, out _);
                    if (nameKeys.IsEmpty)
                        _nameIndex.TryRemove(normalizedName, out _);
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
            var ownedKeys = new HashSet<QualifiedSymbolKey>();
            bool changed = false;

            _fileIndex.TryGetValue(filePath, out var existingOwned);
            var existingOwnedSet = existingOwned?.Keys.ToHashSet() ?? new HashSet<QualifiedSymbolKey>();

            // --- Pass 1: process all submitted symbols ---
            foreach (var def in newSymbolsList)
            {
                var key = QualifiedSymbolKey.Normalized(def.Namespace, def.Name);

                if (_symbols.TryGetValue(key, out var existing))
                {
                    bool sameOwner = string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
                    // Strict > so same-priority files use first-write-wins, not last-write-wins
                    bool strictlyHigherPriority = GetSourcePriority(filePath) > GetSourcePriority(existing.FilePath);

                    if (sameOwner || strictlyHigherPriority)
                    {
                        ownedKeys.Add(key);
                        if (!SymbolsEqual(existing, def))
                        {
                            changed = true;
                            _symbols[key] = def;
                        }
                    }
                    // else: higher-or-equal-priority file owns this key — do not take it
                }
                else
                {
                    ownedKeys.Add(key);
                    changed = true;
                    _symbols[key] = def;

                    var nameSymbols = _nameIndex.GetOrAdd(StringPool.Intern(def.Name.ToLowerInvariant()),
                        _ => new ConcurrentDictionary<QualifiedSymbolKey, byte>());
                    nameSymbols[key] = 0;
                }
            }

            // --- Pass 2: remove owned keys this file no longer exports ---
            foreach (var key in existingOwnedSet.Except(ownedKeys))
            {
                if (!(_symbols.TryGetValue(key, out var owner) &&
                      string.Equals(owner.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                      _symbols.TryRemove(key, out var removed)))
                    continue;

                changed = true;

                var normalizedName = removed.Name.ToLowerInvariant();
                if (_nameIndex.TryGetValue(normalizedName, out var nameKeys))
                {
                    nameKeys.TryRemove(key, out _);
                    if (nameKeys.IsEmpty)
                        _nameIndex.TryRemove(normalizedName, out _);
                }
            }

            // --- Update _fileIndex (owned keys only) ---
            if (ownedKeys.Count > 0)
            {
                var fileSymbols = _fileIndex.GetOrAdd(filePath,
                    _ => new ConcurrentDictionary<QualifiedSymbolKey, byte>());
                fileSymbols.Clear();
                foreach (var key in ownedKeys)
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

    /// <inheritdoc/>
    /// <remarks>
    /// Relies on <c>_fileIndex</c> containing only keys that the file currently *owns*
    /// (i.e. won the priority race). Non-owning submissions are in <c>_submittedIndex</c>
    /// and are intentionally excluded here so flag checks reflect the active definition.
    /// </remarks>
    bool ISymbolLocationProvider.AnyFunctionInFileHasFlag(string filePathSuffix, string flag)
    {
        _lock.EnterReadLock();
        try
        {
            foreach (var (path, keys) in _fileIndex)
            {
                if (!path.EndsWith(filePathSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var key in keys.Keys)
                {
                    if (!_symbols.TryGetValue(key, out var def)) continue;
                    if (def.Type != ExportedSymbolType.Function) continue;
                    if (def.Flags is null) continue;
                    for (int i = 0; i < def.Flags.Length; i++)
                    {
                        if (string.Equals(def.Flags[i], flag, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            return false;
        }
        finally { _lock.ExitReadLock(); }
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

    /// <summary>
    /// Returns all distinct file paths that define a specific function in the given namespace.
    /// Used by the code action handler to suggest <c>#using</c> directives for a qualified
    /// call like <c>ns::func()</c> where the namespace is unknown — only files that actually
    /// export the exact function are returned, preventing spurious suggestions.
    /// </summary>
    /// <param name="namespaceName">The namespace qualifier (case-insensitive).</param>
    /// <param name="functionName">The function name (case-insensitive).</param>
    /// <returns>A list of file paths, or an empty list if no files define this function.</returns>
    public List<string> FindFilesForNamespacedFunction(string namespaceName, string functionName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var key = QualifiedSymbolKey.Normalized(namespaceName, functionName);

        _lock.EnterReadLock();
        try
        {
            if (_symbols.TryGetValue(key, out var def) && def.Type == ExportedSymbolType.Function)
            {
                result.Add(def.FilePath);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        return result.ToList();
    }
}
