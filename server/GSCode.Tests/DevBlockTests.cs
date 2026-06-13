using GSCode.Data;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Dev block (<c>/# ... #/</c>) parsing. The engine tracks the dev section as a flag, not a
/// counter: a redundant <c>/#</c> at script root level inside an open dev section is a no-op,
/// and a single <c>#/</c> closes the whole section. Stock scripts rely on this
/// (e.g. vehicle_shared.gsc's debug section).
/// </summary>
public class DevBlockTests
{
    private static async Task<IReadOnlyList<Diagnostic>> AnalyseAsync(string source)
    {
        Script script = new(new Uri("file:///dev_block_test.gsc"), "gsc");
        await script.ParseAsync(source);
        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());
        return await script.GetDiagnosticsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NestedStatementLevelDevBlock_ParsesCleanly()
    {
        var diagnostics = await AnalyseAsync("""
            /#
            function foo()
            {
                /#
                a = 1;
                #/
                b = 2;
            }
            #/
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task RedundantTopLevelDevBlockOpen_SingleCloseEndsSection()
    {
        // Mirrors stock vehicle_shared.gsc: a '/#' debug section containing functions,
        // a second top-level '/#' before the section ends, and one '#/' closing everything.
        var diagnostics = await AnalyseAsync("""
            /#
            function foo()
            {
                a = 1;
            }

            /#
            function bar()
            {
                b = 2;
            }
            #/

            function baz()
            {
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task UnclosedTopLevelDevBlock_StillReportsExpectedClose()
    {
        var diagnostics = await AnalyseAsync("""
            /#
            function foo()
            {
            }
            """);

        Assert.Contains(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.ExpectedToken));
    }
}
