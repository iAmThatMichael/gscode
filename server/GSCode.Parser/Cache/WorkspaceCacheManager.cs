using GSCode.Parser.SA;
using Serilog;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GSCode.Parser.Cache;

/// <summary>
/// Manages loading and saving the workspace parse cache to disk.
/// Uses gzip-compressed JSON for storage efficiency.
/// </summary>
public static class WorkspaceCacheManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new QualifiedSymbolKeyConverter() }
    };

    /// <summary>
    /// Current format version of the cache schema.
    /// Increment this whenever the cache structure changes in a breaking way.
    /// </summary>
    public const int CacheFormatVersion = 7;

    /// <summary>
    /// Gets the full path to the cache file.
    /// Resolves to %APPDATA%/gscode/cache.db on Windows, ~/.local/share/gscode/cache.db on Linux.
    /// </summary>
    public static string GetCacheFilePath()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string gscodePath = Path.Combine(appDataPath, "gscode");
        Directory.CreateDirectory(gscodePath);
        return Path.Combine(gscodePath, "cache.db");
    }

    /// <summary>
    /// Loads the workspace cache from disk.
    /// Returns null if the cache doesn't exist, is corrupted, or has a version mismatch.
    /// </summary>
    public static async Task<WorkspaceCacheFile?> LoadAsync(
        string cacheFilePath,
        string? expectedServerVersion = null)
    {
        if (!File.Exists(cacheFilePath))
        {
            Log.Information("Cache file not found at {Path}, will build fresh cache", cacheFilePath);
            return null;
        }

        try
        {
            await using FileStream fileStream = File.OpenRead(cacheFilePath);
            await using GZipStream gzipStream = new(fileStream, CompressionMode.Decompress);

            WorkspaceCacheFile? cache = await JsonSerializer.DeserializeAsync<WorkspaceCacheFile>(gzipStream, SerializerOptions);

            if (cache is null)
            {
                Log.Warning("Cache file deserialized to null, discarding");
                return null;
            }

            if (cache.FormatVersion != CacheFormatVersion)
            {
                Log.Warning("Cache format version mismatch (expected {Expected}, got {Actual}), discarding cache",
                    CacheFormatVersion, cache.FormatVersion);
                return null;
            }

            if (expectedServerVersion is not null &&
                !string.Equals(cache.ServerVersion, expectedServerVersion, StringComparison.Ordinal))
            {
                Log.Warning("Cache server version mismatch (expected {Expected}, got {Actual}), discarding cache",
                    expectedServerVersion, cache.ServerVersion);
                return null;
            }

            // System.Text.Json always creates Dictionary<K,V> with the default ordinal comparer.
            // Rebuild Scripts and each entry's DependencyContentHashes with OrdinalIgnoreCase and
            // normalized keys so drive-letter casing differences (G:\ vs g:\) never cause false
            // cache misses — both in the outer lookup and in the per-dependency hash check.
            var normalized = new Dictionary<string, CachedScriptData>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in cache.Scripts)
            {
                string normalizedKey = Path.GetFullPath(key);
                var normalizedDepHashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var (depKey, depHash) in value.DependencyContentHashes)
                {
                    try { normalizedDepHashes[Path.GetFullPath(depKey)] = depHash; }
                    catch { normalizedDepHashes[depKey] = depHash; }
                }
                normalized[normalizedKey] = value with { DependencyContentHashes = normalizedDepHashes };
            }
            cache = cache with { Scripts = normalized };

            Log.Information("Loaded workspace cache with {Count} entries (saved {When})",
                cache.Scripts.Count, cache.LastSaved);
            return cache;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load cache from {Path}, will rebuild", cacheFilePath);
            return null;
        }
    }

    /// <summary>
    /// Saves the workspace cache to disk.
    /// Uses gzip compression to reduce file size.
    /// </summary>
    public static async Task SaveAsync(string cacheFilePath, WorkspaceCacheFile data)
    {
        try
        {
            // Ensure directory exists
            string? directory = Path.GetDirectoryName(cacheFilePath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            // Write to a temporary file first, then replace atomically
            string tempFilePath = cacheFilePath + ".tmp";

            await using (FileStream fileStream = File.Create(tempFilePath))
            await using (GZipStream gzipStream = new(fileStream, CompressionLevel.Fastest))
            {
                await JsonSerializer.SerializeAsync(gzipStream, data, SerializerOptions);
            }

            // Atomic replace
            File.Move(tempFilePath, cacheFilePath, overwrite: true);

            Log.Information("Saved workspace cache with {Count} entries to {Path}",
                data.Scripts.Count, cacheFilePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save cache to {Path}", cacheFilePath);
        }
    }

    /// <summary>
    /// Computes a deterministic hash for a string that is stable across process restarts.
    /// Uses FNV-1a 32-bit hash. Unlike <see cref="string.GetHashCode"/>, this is not randomized.
    /// </summary>
    public static int GetDeterministicHashCode(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash = (hash ^ c) * 16777619;
            }
            return (int)hash;
        }
    }
}
