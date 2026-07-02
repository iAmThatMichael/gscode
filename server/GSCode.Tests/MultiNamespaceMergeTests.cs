using GSCode.Data;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression test: a file with more than one `namespace` directive must merge each
/// function's analysis data (locals, field assignments) under the namespace that was
/// active when *that function* was registered, not under whichever namespace happened to
/// be active last in the file.
/// </summary>
public class MultiNamespaceMergeTests
{
    [Fact]
    public async Task FunctionInEarlierNamespace_RetainsMergedLocals()
    {
        Script script = new(new Uri("file:///multi_ns_test.gsc"), ScriptLanguage.Gsc);
        await script.ParseAsync("""
            #namespace foo;

            function alpha()
            {
                x = 1;
            }

            #namespace bar;

            function beta()
            {
                y = 2;
            }
            """);

        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());

        // BUG (pre-fix): TypeFlowAnalyser.CurrentNamespace is fixed at "bar" (the last
        // namespace directive in the file) for the whole analysis pass, so the merge for
        // "alpha" is attempted under qualifier "bar" instead of "foo", misses, and silently
        // drops alpha's locals.
        var alphaDef = script.DefinitionsTable!.GetFunctionDefinition("foo", "alpha");
        Assert.NotNull(alphaDef);
        Assert.Contains(alphaDef!.Variables, v => v.Name == "x");

        // beta, registered under the final namespace, should be unaffected either way.
        var betaDef = script.DefinitionsTable!.GetFunctionDefinition("bar", "beta");
        Assert.NotNull(betaDef);
        Assert.Contains(betaDef!.Variables, v => v.Name == "y");
    }
}
