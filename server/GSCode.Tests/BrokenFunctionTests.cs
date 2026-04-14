using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace GSCode.Tests;

public class BrokenFunctionTests
{
    [Fact]
    public void BrokenFunctionDiagnostic_IsWarning_AndDeprecated()
    {
        Diagnostic diagnostic = DiagnosticCodes.GetDiagnostic(
            new LspRange(),
            DiagnosticSources.Spa,
            GSCErrorCodes.BrokenFunctionUsage,
            "DoThing");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.NotNull(diagnostic.Tags);
        Assert.Contains(DiagnosticTag.Deprecated, diagnostic.Tags!);
    }

    [Fact]
    public void BrokenFunctionReference_UsesDeprecatedSemanticModifier()
    {
        ScrFunction function = new()
        {
            Name = "DoThing",
            Flags = ["broken"],
            Overloads = [new ScrFunctionOverload()]
        };

        ScrFunctionReferenceSymbol symbol = new(
            new LexemeToken(TokenType.Identifier, new TokenRange(0, 0, 0, 7), "DoThing"),
            function);

        Assert.Contains("deprecated", symbol.SemanticTokenModifiers);
    }
}

