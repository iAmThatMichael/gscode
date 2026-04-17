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
    /// <summary>
    /// Current format version of the cache schema.
    /// Increment this whenever the cache structure changes in a breaking way.
    /// </summary>
    public const int CacheFormatVersion = 1;

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
    public static async Task<WorkspaceCacheFile?> LoadAsync(string cacheFilePath)
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

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() }
            };

            WorkspaceCacheFile? cache = await JsonSerializer.DeserializeAsync<WorkspaceCacheFile>(gzipStream, options);

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
            await using (GZipStream gzipStream = new(fileStream, CompressionLevel.Optimal))
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() }
                };

                await JsonSerializer.SerializeAsync(gzipStream, data, options);
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
}
