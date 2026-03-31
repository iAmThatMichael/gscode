using GSCode.Parser.Data;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

internal record ScrDependencySymbol(Range Range, string Path, string RawPath) : ISenseDefinition
{
    public string SemanticTokenType { get; } = "string";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover() => new()
    {
        Range = Range,
        Contents = new MarkedStringsOrMarkupContent(new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = $"```gsc\n#using {RawPath}\n/* (script) \"{Path}\" */\n```"
        })
    };
}
