using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

internal record ScrClassMemberSymbol(Token NameToken, ScrMember Source, ScrClass ClassSource) : ISenseDefinition
{
    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "property";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover() => new()
    {
        Range = Range,
        Contents = new MarkedStringsOrMarkupContent(new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = Source.Documentation
        })
    };
}
