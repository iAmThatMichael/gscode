using GSCode.Data;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

public class DiagnosticsTests
{
    [Theory]
    [InlineData("""
        function test()
        {
            foo = self;
            foo waittill("foo");
        }
        """)]
    [InlineData("""
        function test()
        {
            foo = self;
            foo waittillmatch("foo", "bar");
        }
        """)]
    [InlineData("""
        function test()
        {
            foo = self;
            foo notify("foo");
        }
        """)]
    [InlineData("""
        function test()
        {
            foo = self;
            foo endon("death");
        }
        """)]
    public async Task UnusedVariable_AssignedValueUsedAsCalledOnReceiver_DoesNotDiagnostic(string source)
    {
        IReadOnlyList<Diagnostic> diagnostics = await AnalyseDiagnosticsAsync(source);

        AssertDoesNotContainUnusedVariable(diagnostics, "foo");
    }

    [Theory]
    [InlineData("""
        function test()
        {
            eventName = "foo";
            self waittill(eventName);
        }
        """, "eventName")]
    [InlineData("""
        function test()
        {
            eventName = "foo";
            self waittillmatch(eventName);
        }
        """, "eventName")]
    [InlineData("""
        function test()
        {
            matchValue = "bar";
            self waittillmatch("foo", matchValue);
        }
        """, "matchValue")]
    public async Task UnusedVariable_AssignedValueUsedInsideWaitExpression_DoesNotDiagnostic(
        string source,
        string variableName)
    {
        IReadOnlyList<Diagnostic> diagnostics = await AnalyseDiagnosticsAsync(source);

        AssertDoesNotContainUnusedVariable(diagnostics, variableName);
    }

    [Fact]
    public async Task UnusedVariable_AssignedFunctionPointerUsedInDerefCall_DoesNotDiagnostic()
    {
        IReadOnlyList<Diagnostic> diagnostics = await AnalyseDiagnosticsAsync("""
            function helper()
            {
            }

            function test()
            {
                callback = &helper;
                [[callback]]();
            }
            """);

        AssertDoesNotContainUnusedVariable(diagnostics, "callback");
    }

    private static async Task<IReadOnlyList<Diagnostic>> AnalyseDiagnosticsAsync(string source)
    {
        Script script = new(new Uri("file:///test.gsc"), ScriptLanguage.Gsc);
        await script.ParseAsync(source);
        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());
        return await script.GetDiagnosticsAsync(CancellationToken.None);
    }

    private static void AssertDoesNotContainUnusedVariable(
        IReadOnlyList<Diagnostic> diagnostics,
        string variableName)
    {
        Assert.DoesNotContain(diagnostics, diagnostic =>
            Equals(diagnostic.Code, (int)GSCErrorCodes.UnusedVariable)
            && diagnostic.Message.Contains($"'{variableName}'", StringComparison.OrdinalIgnoreCase));
    }
}

