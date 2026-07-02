using GSCode.Data;
using GSCode.Parser;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression test: InsertPaths must reflect only the #insert directives present in the
/// most recent parse, not accumulate every path ever seen across the script's lifetime.
/// </summary>
public class ScriptInsertPathsTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _aPath;
    private readonly string _bPath;

    public ScriptInsertPathsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "gscode_insert_test_" + Guid.NewGuid().ToString("N"));
        string scriptsDir = Path.Combine(_rootDir, "scripts");
        Directory.CreateDirectory(scriptsDir);
        _aPath = Path.Combine(scriptsDir, "a.gsh");
        _bPath = Path.Combine(scriptsDir, "b.gsh");
        File.WriteAllText(_aPath, "#define A_MACRO 1\n");
        File.WriteAllText(_bPath, "#define B_MACRO 1\n");
    }

    [Fact]
    public async Task SwitchingInsertTarget_ReplacesInsertPaths()
    {
        string hostPath = Path.Combine(_rootDir, "scripts", "host.gsc");
        Script script = new(new Uri(hostPath), ScriptLanguage.Gsc);

        await script.ParseAsync("""
            #insert scripts\a.gsh;

            function main() {}
            """);
        Assert.Contains(script.InsertPaths, p => p.EndsWith("a.gsh", StringComparison.OrdinalIgnoreCase));

        await script.ParseAsync("""
            #insert scripts\b.gsh;

            function main() {}
            """);

        // BUG (pre-fix): InsertPaths still contains "a.gsh" from the previous cycle, and
        // now has 2 entries instead of 1.
        Assert.DoesNotContain(script.InsertPaths, p => p.EndsWith("a.gsh", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(script.InsertPaths, p => p.EndsWith("b.gsh", StringComparison.OrdinalIgnoreCase));
        Assert.Single(script.InsertPaths);
    }

    public void Dispose()
    {
        Script.InvalidateCachedFile(_aPath);
        Script.InvalidateCachedFile(_bPath);
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }
}
