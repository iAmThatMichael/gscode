using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Serilog;

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
        // Normalize the source file path to avoid duplicates due to casing/separator differences
        // Use lowercase and forward slashes for consistency
        string cacheFilePath;
        if (sourceFilePath != null)
        {
            cacheFilePath = NormalizePath(sourceFilePath);
        }
        else
        {
            cacheFilePath = "<built-in>";
        }

        // Create cache key based on source file and macro name only
        // DefineSnippet is not included because it may vary due to token ranges
        // even when the macro content is identical
        MacroCacheKey key = new(cacheFilePath, macroName);

        // Get or add to cache
        MacroDefinition cached = _cache.GetOrAdd(key, definition);

//#if FLAG_MEMORY_DEBUG
//        // Log every 100th addition to trace the pattern
//        bool keyExisted = _cache.ContainsKey(key);
//        if (!keyExisted && _cache.Count % 100 == 0)
//        {
//            Log.Debug("[MACRO_CACHE_ADD] #{Count} | Key: {Path}::{Macro} | SourceInput: {RawSource}", 
//                _cache.Count, cacheFilePath, macroName, sourceFilePath ?? "<null>");
//        }
//        // Log duplicates occasionally
//        if (keyExisted && _cache.Count % 1000 == 0)
//        {
//            Log.Debug("[MACRO_CACHE_DUP] Total: {Count} | Duplicate key: {Path}::{Macro}", 
//                _cache.Count, cacheFilePath, macroName);
//        }
//#endif

        // Track which file uses this macro (for cleanup)
        if (sourceFilePath != null)
        {
            _fileToMacros.AddOrUpdate(
                cacheFilePath, // Use normalized path for tracking too
                _ => new HashSet<MacroCacheKey> { key },
                (_, set) => { lock (set) { set.Add(key); } return set; }
            );
        }

        return cached;
    }

    /// <summary>
    /// Normalizes a file path to ensure consistent caching regardless of casing or separators.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string NormalizePath(string path)
    {
        // Convert to lowercase and use forward slashes for consistency
        // This ensures G:/path/file.gsh and g:\path\file.gsh are treated as the same
        return path.ToLowerInvariant().Replace('\\', '/');
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
    /// Uniqueness is based on source file and macro name only.
    /// Within a single file, macro names must be unique (no redefinitions allowed).
    /// </summary>
    private readonly record struct MacroCacheKey(string SourceFile, string MacroName);
}
