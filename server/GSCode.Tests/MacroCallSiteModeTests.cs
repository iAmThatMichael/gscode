using GSCode.Data;
using GSCode.Parser;
using GSCode.Parser.Data;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression tests for AddMacroCallSite's mode gating: call-site tracking must still work
/// in Editor mode (used by SignatureHelp) and must not run in Index mode, where
/// Sense.Completions is never populated and the work would be wasted.
/// </summary>
public class MacroCallSiteModeTests
{
    private const string Source = """
        #define ADD(a, b) ((a) + (b))

        function main()
        {
            x = ADD(1, 2);
        }
        """;

    [Fact]
    public async Task EditorMode_StillRecordsMacroCallSite()
    {
        Script script = new(new Uri("file:///macro_callsite_editor_test.gsc"), ScriptLanguage.Gsc, mode: ScriptMode.Editor);
        await script.ParseAsync(Source);

        Assert.NotNull(script.Sense.Completions);
        Assert.NotEmpty(script.Sense.Completions!.MacroCallSites);
    }

    [Fact]
    public async Task IndexMode_DoesNotPopulateCompletions()
    {
        Script script = new(new Uri("file:///macro_callsite_index_test.gsc"), ScriptLanguage.Gsc, mode: ScriptMode.Index);
        await script.ParseAsync(Source);

        Assert.Null(script.Sense.Completions);
    }
}
