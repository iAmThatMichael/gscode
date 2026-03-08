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
    /// Cache key: (source file path, macro name, define snippet content)
    /// This allows the same macro name from different files to coexist.
    /// </summary>
    private readonly ConcurrentDictionary<MacroCacheKey, MacroDefinition> _cache = new();

    /// <summary>
    /// Per-file tracking of which macros came from which source.
    /// Used for cleanup when files are closed/removed.
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<MacroCacheKey>> _fileToMacros = new();

    private MacroDefinitionCache() { }

    /// <summary>
    /// Gets or creates a cached macro definition.
    /// If an identical macro already exists in the cache, returns the existing instance.
    /// </summary>
    /// <param name="sourceFilePath">The file path where this macro is defined (null for built-in macros)</param>
    /// <param name="macroName">The name of the macro</param>
    /// <param name="definition">The macro definition to cache</param>
    /// <returns>The cached macro definition (may be the same instance or an existing one)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MacroDefinition GetOrAdd(string? sourceFilePath, string macroName, MacroDefinition definition)
    {
        // For built-in macros, don't cache by file path
        string cacheFilePath = sourceFilePath ?? "<built-in>";
        
        // Create cache key based on content to ensure uniqueness
        MacroCacheKey key = new(cacheFilePath, macroName, definition.DefineSnippet);

        // Get or add to cache
        MacroDefinition cached = _cache.GetOrAdd(key, definition);

        // Track which file uses this macro (for cleanup)
        if (sourceFilePath != null)
        {
            _fileToMacros.AddOrUpdate(
                sourceFilePath,
                _ => new HashSet<MacroCacheKey> { key },
                (_, set) => { lock (set) { set.Add(key); } return set; }
            );
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
        if (_fileToMacros.TryRemove(sourceFilePath, out HashSet<MacroCacheKey>? keys))
        {
            foreach (var key in keys)
            {
                _cache.TryRemove(key, out _);
            }
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
    /// Gets the current cache statistics for debugging/monitoring.
    /// </summary>
    public (int TotalMacros, int TrackedFiles) GetStatistics()
    {
        return (_cache.Count, _fileToMacros.Count);
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
        return (_cache.Count, _fileToMacros.Count, gshCount);
    }

    /// <summary>
    /// Cache key for macro definitions.
    /// </summary>
    private readonly record struct MacroCacheKey(string SourceFile, string MacroName, string DefineSnippet);
}
