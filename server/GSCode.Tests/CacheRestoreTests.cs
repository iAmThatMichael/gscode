using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Parser;
using GSCode.Parser.Cache;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
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
            FunctionDefinitions = [],
            ClassDefinitions = [],
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

    [Fact]
    public void RestoreFromCache_MergesOverloadsForDuplicateExportedFunctionNames()
    {
        Script script = new(new Uri("file:///test.gsc"), ScriptLanguage.Gsc, mode: ScriptMode.Index);

        var firstOverloadSet = new ScrFunction { Name = "foo", Namespace = "test" };
        firstOverloadSet.Overloads.Add(new ScrFunctionOverload());

        var secondOverloadSet = new ScrFunction { Name = "foo", Namespace = "test" };
        secondOverloadSet.Overloads.Add(new ScrFunctionOverload());

        var cacheData = new CachedScriptData
        {
            ContentHash = 1,
            LanguageId = "gsc",
            CachedAt = DateTime.UtcNow,
            CurrentNamespace = "test",
            ExportedFunctions = [firstOverloadSet, secondOverloadSet],
            ExportedClasses = [],
            Dependencies = [],
            DependencyContentHashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            FunctionLocations = [],
            ClassLocations = [],
            FunctionDefinitions = [],
            ClassDefinitions = [],
            MacroDefinitions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            Diagnostics = [],
            References = [],
            GlobalFieldAccesses = []
        };

        script.RestoreFromCache(cacheData, ScriptMode.Index);

        // Same invariant AddFunction enforces during a live parse: one merged entry, not two.
        Assert.Single(script.DefinitionsTable!.ExportedFunctions, f => f.Name == "foo");
        Assert.True(script.DefinitionsTable!.ExportedSymbols.TryGetValue("foo", out var merged));
        Assert.Equal(2, ((ScrFunction)merged!).Overloads.Count);
    }

    [Fact]
    public void RestoreFromCache_InternsExportedFunctionNameAndNamespace()
    {
        Script script = new(new Uri("file:///test.gsc"), ScriptLanguage.Gsc, mode: ScriptMode.Index);

        // Build the cached function's Name/Namespace from freshly allocated strings so they are
        // guaranteed not to already be reference-equal to whatever StringPool.Intern returns.
        string freshName = new(['b', 'a', 'r']);
        string freshNamespace = new(['t', 'e', 's', 't']);
        var func = new ScrFunction { Name = freshName, Namespace = freshNamespace };

        var cacheData = new CachedScriptData
        {
            ContentHash = 1,
            LanguageId = "gsc",
            CachedAt = DateTime.UtcNow,
            CurrentNamespace = "test",
            ExportedFunctions = [func],
            ExportedClasses = [],
            Dependencies = [],
            DependencyContentHashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            FunctionLocations = [],
            ClassLocations = [],
            FunctionDefinitions = [],
            ClassDefinitions = [],
            MacroDefinitions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            Diagnostics = [],
            References = [],
            GlobalFieldAccesses = []
        };

        script.RestoreFromCache(cacheData, ScriptMode.Index);

        var restored = Assert.Single(script.DefinitionsTable!.ExportedFunctions);
        Assert.Same(StringPool.Intern(freshName), restored.Name);
        Assert.Same(StringPool.Intern(freshNamespace), restored.Namespace);
    }
}
