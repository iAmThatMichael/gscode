using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.SA;

internal record ScrClassSymbol(Token NameToken, ScrClass Source) : ISenseDefinition
{
    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "class";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover() => new()
    {
        Range = Range,
        Contents = new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = $"```gsc\nclass {Source.Name}\n```"
        }
    };
}

internal record ScrClassReferenceSymbol(Token NameToken, string ClassName) : ISenseDefinition
{
    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "class";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover() => new()
    {
        Range = Range,
        Contents = new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = $"```gsc\nclass {ClassName}\n```"
        }
    };
}
