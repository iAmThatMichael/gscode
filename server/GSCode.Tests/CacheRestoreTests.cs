using GSCode.Data;
using GSCode.Parser;
using GSCode.Parser.Cache;
using GSCode.Parser.Data;
using GSCode.Parser.SA;
using Xunit;

namespace GSCode.Tests;

public class CacheRestoreTests
{
    [Fact]
    public void RestoreFromCache_RestoresReferenceAndGlobalFieldIndexes()
    {
        Script script = new(new Uri("file:///test.gsc"), ScriptLanguage.Gsc, mode: ScriptMode.Index);
        var reference = new CachedReference(
            SymbolKind.Function,
            "test",
            "helper",
            ClassName: null,
            ScopeId: null,
            StartLine: 3,
            StartChar: 8,
            EndLine: 3,
            EndChar: 14);

        var cacheData = new CachedScriptData
        {
            ContentHash = 1,
            LanguageId = "gsc",
            CachedAt = DateTime.UtcNow,
            CurrentNamespace = "test",
            ExportedFunctions = [],
            ExportedClasses = [],
            Dependencies = [],
            DependencyContentHashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            FunctionLocations = [],
            ClassLocations = [],
            FunctionParameters = [],
            FunctionFlags = [],
            FunctionDocs = [],
            MacroDefinitions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            Diagnostics = [],
            References = [reference],
            GlobalFieldAccesses = [new CachedGlobalFieldAccess("level", "score")]
        };

        script.RestoreFromCache(cacheData, ScriptMode.Index);

        var key = new SymbolKey(SymbolKind.Function, "test", "helper");
        Assert.True(script.References.TryGetValue(key, out var ranges));
        Assert.Single(ranges);
        Assert.Contains(("level", "score"), script.ExtractGlobalFieldAccesses());
    }
}
