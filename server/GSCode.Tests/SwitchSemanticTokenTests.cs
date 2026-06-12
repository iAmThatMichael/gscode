using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// The switch subject expression is only type-analysed on the first (silent) worklist visit,
/// so the final diagnostics pass must re-analyse it to emit sense tokens — otherwise the
/// variable in <c>switch (x)</c> gets no semantic highlighting.
/// </summary>
public class SwitchSemanticTokenTests
{
    [Fact]
    public async Task SwitchSubjectVariable_GetsSemanticToken()
    {
        string source = """
            function test(a)
            {
                switch (a)
                {
                    case 1:
                        b = 1;
                        break;
                    default:
                        break;
                }
            }
            """;
        Script script = new(new Uri("file:///switch_token_test.gsc"), "gsc");
        await script.ParseAsync(source);
        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());
        var tokens = await script.GetSemanticTokensAsync(CancellationToken.None);

        // 'a' in 'switch (a)' is on line index 2, columns 12-13.
        Assert.Contains(tokens, t =>
            t.Range.Start.Line == 2 && t.Range.Start.Character == 12 &&
            t.SemanticTokenType == "variable");
    }
}
