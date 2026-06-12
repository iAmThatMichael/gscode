using GSCode.Parser.Cache;
using Xunit;

namespace GSCode.Tests;

public class WorkspaceCacheManagerTests
{
    [Fact]
    public async Task LoadAsync_WhenServerVersionDoesNotMatch_ReturnsNull()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "gscode-cache-tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "cache.db");

        try
        {
            var cacheFile = new WorkspaceCacheFile
            {
                FormatVersion = WorkspaceCacheManager.CacheFormatVersion,
                ServerVersion = "old-server",
                LastSaved = DateTime.UtcNow,
                Scripts = new Dictionary<string, CachedScriptData>(StringComparer.OrdinalIgnoreCase)
            };

            await WorkspaceCacheManager.SaveAsync(path, cacheFile);

            WorkspaceCacheFile? loaded = await WorkspaceCacheManager.LoadAsync(path, "new-server");

            Assert.Null(loaded);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
