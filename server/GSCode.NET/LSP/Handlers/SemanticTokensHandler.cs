using GSCode.Parser;
using GSCode.Parser.Data;
using System.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;

namespace GSCode.NET.LSP.Handlers;

internal class SemanticTokensHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : SemanticTokensHandlerBase
{

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

    private static readonly SemanticTokenModifier[] s_tokenModifiers =
    [
        SemanticTokenModifier.Declaration, SemanticTokenModifier.Definition,
        SemanticTokenModifier.Readonly,    SemanticTokenModifier.Static,
        SemanticTokenModifier.Deprecated,  SemanticTokenModifier.Abstract,
        SemanticTokenModifier.Async,       SemanticTokenModifier.Modification,
        SemanticTokenModifier.Documentation, SemanticTokenModifier.DefaultLibrary
    ];

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = documentSelector,
            Legend = new SemanticTokensLegend
            {
                TokenTypes = new Container<SemanticTokenType>(s_tokenTypes),
                TokenModifiers = new Container<SemanticTokenModifier>(s_tokenModifiers)
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
        Script? script = scriptManager.GetParsedEditor(identifier.TextDocument);
        if (script is null) return;

        var tokens = await script.GetSemanticTokensAsync(cancellationToken);

        // Tokens are pre-sorted by FinalizeSemanticTokens during analysis (line/character order).
        foreach (var token in tokens)
        {
            int length = token.Range.End.Character - token.Range.Start.Character;
            SemanticTokenType tokenType = new(token.SemanticTokenType);
            SemanticTokenModifier[] modifiers = token.SemanticTokenModifiers
                .Select(m => new SemanticTokenModifier(m))
                .ToArray();

            builder.Push(token.Range.Start.Line, token.Range.Start.Character, length, tokenType, modifiers);
        }

        Log.Information("Tokenization is complete!");
    }
}
