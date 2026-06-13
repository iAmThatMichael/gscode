using GSCode.Parser;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Issue #66: changes to a GSH file must be visible to scripts that #insert it.
/// The lexed-token and macro-definition caches are static and shared across parses, so a
/// re-parse after an external GSH change replays stale content unless
/// <see cref="Script.InvalidateCachedFile"/> is called first (the DidChangeWatchedFiles
/// handler does this for every changed script file).
/// </summary>
public class GshInvalidationTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _hostPath;
    private readonly string _gshPath;

    public GshInvalidationTests()
    {
        // #insert resolution requires the host script to live under a "scripts" folder.
        _rootDir = Path.Combine(Path.GetTempPath(), "gscode_gsh_test_" + Guid.NewGuid().ToString("N"));
        string scriptsDir = Path.Combine(_rootDir, "scripts");
        Directory.CreateDirectory(scriptsDir);
        _hostPath = Path.Combine(scriptsDir, "host.gsc");
        _gshPath = Path.Combine(scriptsDir, "header.gsh");
    }

    public void Dispose()
    {
        Script.InvalidateCachedFile(_gshPath);
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private async Task<Script> ParseHostAsync()
    {
        string source = """
            #insert scripts\header.gsh;

            function main()
            {
            }
            """;
        File.WriteAllText(_hostPath, source);
        Script script = new(new Uri(_hostPath), "gsc");
        await script.ParseAsync(source);
        return script;
    }

    [Fact]
    public async Task ChangedGshMacroSet_IsPickedUpAfterInvalidation()
    {
        File.WriteAllText(_gshPath, "#define OLD_MACRO 1\n");
        Script first = await ParseHostAsync();
        Assert.Contains("OLD_MACRO", first.GetMacroSourcePaths().Keys);

        File.WriteAllText(_gshPath, "#define NEW_MACRO 1\n");
        Script.InvalidateCachedFile(_gshPath);

        Script second = await ParseHostAsync();
        var macros = second.GetMacroSourcePaths();
        Assert.Contains("NEW_MACRO", macros.Keys);
        Assert.DoesNotContain("OLD_MACRO", macros.Keys);
    }

    [Fact]
    public async Task ChangedGshMacroBody_IsPickedUpAfterInvalidation()
    {
        File.WriteAllText(_gshPath, "#define VALUE 1\n");
        Script first = await ParseHostAsync();
        Assert.Contains("1", first.Sense.MacroDefinitions["VALUE"].Definition.DefineSnippet);

        File.WriteAllText(_gshPath, "#define VALUE 2\n");
        Script.InvalidateCachedFile(_gshPath);

        Script second = await ParseHostAsync();
        Assert.Contains("2", second.Sense.MacroDefinitions["VALUE"].Definition.DefineSnippet);
    }

    [Fact]
    public async Task InvalidationKey_AcceptsOsNativePath()
    {
        // The resolver produces mixed-separator paths for cache keys; the watcher hands the
        // handler an OS-native path. Invalidation must bridge the two via normalization.
        File.WriteAllText(_gshPath, "#define FIRST 1\n");
        await ParseHostAsync();

        File.WriteAllText(_gshPath, "#define SECOND 1\n");
        Script.InvalidateCachedFile(_gshPath.Replace('\\', '/'));

        Script second = await ParseHostAsync();
        Assert.Contains("SECOND", second.GetMacroSourcePaths().Keys);
    }
}
