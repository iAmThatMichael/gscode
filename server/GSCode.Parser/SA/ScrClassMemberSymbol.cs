using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.SA;

internal record ScrClassMemberSymbol(Token NameToken, ScrMember Source, ScrClass ClassSource) : ISenseDefinition
{
    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "property";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover() => new()
    {
        Range = Range,
        Contents = new MarkupContent()
        {
            Kind = MarkupKind.Markdown,
            Value = Source.Documentation
        }
    };
}


