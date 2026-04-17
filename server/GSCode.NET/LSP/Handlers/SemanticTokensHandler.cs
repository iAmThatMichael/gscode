using GSCode.Parser;
using GSCode.Parser.Data;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

internal class SemanticTokensHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : SemanticTokensHandlerBase
{
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    private static readonly SemanticTokenType[] s_tokenTypes =
    [
        SemanticTokenType.Namespace, SemanticTokenType.Type,     SemanticTokenType.Class,
        SemanticTokenType.Enum,      SemanticTokenType.Interface, SemanticTokenType.Struct,
        SemanticTokenType.TypeParameter, SemanticTokenType.Parameter, SemanticTokenType.Variable,
        SemanticTokenType.Property,  SemanticTokenType.EnumMember, SemanticTokenType.Event,
        SemanticTokenType.Function,  SemanticTokenType.Method,   SemanticTokenType.Macro,
        SemanticTokenType.Keyword,   SemanticTokenType.Modifier, SemanticTokenType.Comment,
        SemanticTokenType.String,    SemanticTokenType.Number,   SemanticTokenType.Regexp,
        SemanticTokenType.Operator
    ];

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _documentSelector,
            Legend = new SemanticTokensLegend
            {
                TokenTypes = new Container<SemanticTokenType>(s_tokenTypes),
                TokenModifiers = new Container<SemanticTokenModifier>()
            },
            Full = new SemanticTokensCapabilityRequestFull { Delta = false },
            Range = false
        };

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params, CancellationToken cancellationToken)
        => Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));

    protected override async Task Tokenize(SemanticTokensBuilder builder,
        ITextDocumentIdentifierParams identifier, CancellationToken cancellationToken)
    {
        Log.Information("Tokenization request received, processing...");
        Script? script = _scriptManager.GetParsedEditor(identifier.TextDocument);
        if (script is null) return;

        var tokens = await script.GetSemanticTokensAsync(cancellationToken);

        // Sort tokens chronologically (required by OmniSharp)
        var sorted = tokens
            .OrderBy(t => t.Range.Start.Line)
            .ThenBy(t => t.Range.Start.Character)
            .ToList();

        foreach (var token in sorted)
        {
            int length = token.Range.End.Character - token.Range.Start.Character;
            SemanticTokenType tokenType = new(token.SemanticTokenType);
            builder.Push(token.Range.Start.Line, token.Range.Start.Character, length, tokenType, Array.Empty<SemanticTokenModifier>());
        }

        Log.Information("Tokenization is complete!");
    }
}
