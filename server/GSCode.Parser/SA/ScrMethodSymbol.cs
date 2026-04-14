using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.SA;

internal record ScrMethodSymbol(Token NameToken, ScrFunction Source, ScrClass ClassSource) : ScrFunctionSymbol(NameToken, Source)
{
    public override Range Range { get; } = NameToken.Range;

    public override string SemanticTokenType { get; } = "method";
    public override string[] SemanticTokenModifiers { get; } = [];

    public override Hover GetHover() => new()
    {
        Range = Range,
        Contents = new MarkupContent()
        {
            Kind = MarkupKind.Markdown,
            Value = Source.Documentation
        }
    };
}


