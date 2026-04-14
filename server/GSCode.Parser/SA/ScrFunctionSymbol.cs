using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.SA;

internal record ScrFunctionSymbol(Token NameToken, ScrFunction Source) : ISenseDefinition
{
    public virtual Range Range { get; } = NameToken.Range;

    public virtual string SemanticTokenType { get; } = "function";
    public virtual string[] SemanticTokenModifiers { get; } = [];

    public virtual Hover GetHover() => new()
    {
        Range = Range,
        Contents = new MarkupContent()
        {
            Kind = MarkupKind.Markdown,
            Value = Source.Documentation
        }
    };
}


