using GSCode.Parser.Util;
using Serilog;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace GSCode.Parser.Pre;

/// <summary>
/// Global cache for macro definitions to avoid duplicating identical macros across files.
/// When multiple files #insert the same header, they will share the same MacroDefinition instances.
/// </summary>
public sealed class MacroDefinitionCache
{
    private static readonly MacroDefinitionCache _instance = new();
    
    /// <summary>
    /// Gets the singleton instance of the macro definition cache.
    /// </summary>
    public static MacroDefinitionCache Instance => _instance;

    /// <summary>
    /// Cache key: (source file path, macro name).
    /// This allows the same macro name from different files to coexist.
    /// </summary>
    private readonly ConcurrentDictionary<MacroCacheKey, MacroDefinition> _cache = new();

    /// <summary>
    /// Per-file tracking of which macros came from which source.
    /// Used for cleanup when files are closed/removed.
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<MacroCacheKey>> _fileToMacros = new(StringComparer.OrdinalIgnoreCase);

    private MacroDefinitionCache() { }

    /// <summary>
    /// Gets or creates a cached macro definition.
    /// If an identical macro already exists in the cache, returns the existing instance.
    /// </summary>
    /// <param name="sourceFilePath">The file path where this macro is defined (null for built-in macros)</param>
    /// <param name="macroName">The name of the macro</param>
    /// <param name="definition">The macro definition to cache</param>
    /// <returns>The cached macro definition (may be the same instance or an existing one)</returns>
    internal MacroDefinition GetOrAdd(string? sourceFilePath, string macroName, MacroDefinition definition)
    {
        // Normalize path separators for consistent cache key comparison
        string cacheFilePath = sourceFilePath is not null
            ? ScriptFileResolver.NormalizeFilePathForUri(sourceFilePath)
            : "<built-in>";

        MacroCacheKey key = new(cacheFilePath, macroName);

        // Reuse the cached instance only while the definition's content is unchanged.
        // A macro re-defined with a new body (file edited in the editor or changed on
        // disk) must replace the stale entry, or every consumer keeps expanding the
        // old tokens and showing the old hover snippet.
        MacroDefinition cached = _cache.AddOrUpdate(
            key,
            definition,
            (_, existing) => string.Equals(existing.DefineSnippet, definition.DefineSnippet, StringComparison.Ordinal)
                ? existing
                : definition);

        // Track which file owns this macro (for cleanup).
        // Always lock the set before mutating — the same set instance is shared
        // across concurrent GetOrAdd and RemoveFileMacros calls.
        if (sourceFilePath != null)
        {
            HashSet<MacroCacheKey> set = _fileToMacros.GetOrAdd(cacheFilePath, _ => []);
            lock (set)
            {
                set.Add(key);
            }
        }

        return cached;
    }

    /// <summary>
    /// Removes all macros associated with a specific source file.
    /// Call this when a file is closed or removed from the workspace.
    /// </summary>
    /// <param name="sourceFilePath">The file path to remove macros for</param>
    public void RemoveFileMacros(string sourceFilePath)
    {
        // Normalize before lookup — keys were stored with NormalizeFilePathForUri in GetOrAdd.
        // A mismatch here (e.g. different slashes or casing) causes TryRemove to miss the
        // entry entirely, leaving orphaned macros in _cache and a stale GSH count.
        string normalizedPath = ScriptFileResolver.NormalizeFilePathForUri(sourceFilePath);

        if (!_fileToMacros.TryRemove(normalizedPath, out HashSet<MacroCacheKey>? keys))
            return;

        // Lock the set before iterating — a concurrent GetOrAdd may still hold
        // the same set instance and be mid-write when TryRemove returns.
        lock (keys)
        {
            foreach (var key in keys)
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Registers a macro name/source association for tracking purposes (e.g., on cache restore),
    /// without requiring a full MacroDefinition object. Only updates _fileToMacros so that
    /// GetDetailedStatistics() correctly counts GSH files and macro totals after a cache load.
    /// </summary>
    /// <param name="sourceFilePath">The file that defines the macro (null for built-ins, skipped).</param>
    /// <param name="macroName">The macro name.</param>
    public void TrackMacroSource(string? sourceFilePath, string macroName)
    {
        if (sourceFilePath is null) return;

        string cacheFilePath = ScriptFileResolver.NormalizeFilePathForUri(sourceFilePath);
        MacroCacheKey key = new(cacheFilePath, macroName);

        HashSet<MacroCacheKey> set = _fileToMacros.GetOrAdd(cacheFilePath, _ => []);
        lock (set)
        {
            set.Add(key);
        }
    }

    /// <summary>
    /// Clears the entire cache. Use sparingly (e.g., on workspace reload).
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        _fileToMacros.Clear();
    }

    /// <summary>
    /// Gets detailed statistics including GSH file count.
    /// </summary>
    public (int TotalMacros, int TrackedFiles, int GshFiles) GetDetailedStatistics()
    {
        int gshCount = 0;
        foreach (var filePath in _fileToMacros.Keys)
        {
            if (filePath.EndsWith(".gsh", StringComparison.OrdinalIgnoreCase))
            {
                gshCount++;
            }
        }
        // Count total macros from _fileToMacros set sizes: this includes both fully-cached
        // macros (registered via GetOrAdd) and tracking-only entries added via TrackMacroSource
        // on cache restore.  Using _cache.Count would miss the latter.
        int totalMacros = 0;
        foreach (var set in _fileToMacros.Values)
        {
            lock (set)
            {
                totalMacros += set.Count;
            }
        }
        return (totalMacros, _fileToMacros.Count, gshCount);
    }

    /// <summary>
    /// Cache key for macro definitions.
    /// Uniqueness is based on source file and macro name only.
    /// Within a single file, macro names must be unique (no redefinitions allowed).
    /// </summary>
    private readonly record struct MacroCacheKey(string SourceFile, string MacroName) : IEquatable<MacroCacheKey>
    {
        public bool Equals(MacroCacheKey other) =>
            string.Equals(SourceFile, other.SourceFile, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MacroName, other.MacroName, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(SourceFile),
                StringComparer.OrdinalIgnoreCase.GetHashCode(MacroName));
    }
}
