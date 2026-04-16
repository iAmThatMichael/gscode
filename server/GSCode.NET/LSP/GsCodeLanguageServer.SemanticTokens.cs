using Serilog;
using GSCode.Parser;
using GSCode.Parser.Data;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Linq;

namespace GSCode.NET.LSP;

public sealed partial class GsCodeLanguageServer
{
    // -------------------------------------------------------------------------
    // textDocument/semanticTokens/full
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentSemanticTokensFullName, UseSingleObjectParameterDeserialization = true)]
    public async Task<SemanticTokens?> SemanticTokensFullAsync(SemanticTokensParams @params, CancellationToken ct)
    {
        Log.Information("Tokenization request received, processing...");
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return null;
        var tokens = await script.GetSemanticTokensAsync(ct);
        var result = EncodeSemanticTokens(tokens, _semanticTokensLegend);
        Log.Information("Tokenization is complete!");
        return result;
    }

    // -------------------------------------------------------------------------
    // Semantic token encoding
    // -------------------------------------------------------------------------

    private static SemanticTokensLegend BuildLegend() => new()
    {
        TokenTypes =
        [
            SemanticTokenTypes.Namespace, SemanticTokenTypes.Type,     SemanticTokenTypes.Class,
            SemanticTokenTypes.Enum,      SemanticTokenTypes.Interface, SemanticTokenTypes.Struct,
            SemanticTokenTypes.TypeParameter, SemanticTokenTypes.Parameter, SemanticTokenTypes.Variable,
            SemanticTokenTypes.Property,  SemanticTokenTypes.EnumMember, SemanticTokenTypes.Event,
            SemanticTokenTypes.Function,  SemanticTokenTypes.Method,   SemanticTokenTypes.Macro,
            SemanticTokenTypes.Keyword,   SemanticTokenTypes.Modifier, SemanticTokenTypes.Comment,
            SemanticTokenTypes.String,    SemanticTokenTypes.Number,   SemanticTokenTypes.Regexp,
            SemanticTokenTypes.Operator
        ],
        TokenModifiers = []
    };

    private static SemanticTokens EncodeSemanticTokens(
        IReadOnlyList<ISemanticToken> tokens,
        SemanticTokensLegend legend)
    {
        var typeIndex = legend.TokenTypes
            .Select((t, i) => (t, i))
            .ToDictionary(x => x.t, x => x.i, StringComparer.OrdinalIgnoreCase);

        var data = new List<int>(tokens.Count * 5);
        int prevLine = 0, prevChar = 0;

        foreach (var token in tokens)
        {
            if (!typeIndex.TryGetValue(token.SemanticTokenType, out int typeIdx)) continue;

            int line      = token.Range.Start.Line;
            int startChar = token.Range.Start.Character;
            int length    = token.Range.End.Character - token.Range.Start.Character;

            data.Add(line - prevLine);
            data.Add(line == prevLine ? startChar - prevChar : startChar);
            data.Add(length);
            data.Add(typeIdx);
            data.Add(0); // no token modifiers

            prevLine = line;
            prevChar = startChar;
        }

        return new SemanticTokens { Data = data.ToArray() };
    }
}
