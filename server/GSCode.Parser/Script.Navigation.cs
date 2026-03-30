using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Pre;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using System.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using GSCode.Parser.Misc;
using System.IO;
using GSCode.Parser.SPA;
using Serilog;

namespace GSCode.Parser;

public partial class Script
{
    public async Task<Location?> GetDefinitionAsync(Position position, CancellationToken cancellationToken = default)
    {
        if (!Sense.IsEditorMode) return null;
        await WaitUntilParsedAsync(cancellationToken);

        // When Go-to-Definition is triggered with a text selection active the LSP position
        // is the selection end, which may land exactly on the first character of the next
        // token (e.g. '(' after a function name, or a space after a macro). Normalise the
        // position back to the preceding identifier so all subsequent lookups succeed.
        position = AdjustPositionForSelectionEnd(position);

        if (TryGetMacroDefinitionLocation(position) is { } macroLoc)
            return macroLoc;

        int tokenIdx = Sense.Tokens.GetIndex(position);
        Token? token = Sense.Tokens.GetAt(tokenIdx);
        if (token is null) return null;

        return TryGetSymbolDefinitionLocation(token)
            ?? TryGetFileReferenceLocation(position, token, tokenIdx)
            ?? (token.Type == TokenType.Identifier ? TryGetFunctionOrClassLocation(tokenIdx) : null);
    }

    public async Task<(string? qualifier, string name)?> GetQualifiedIdentifierAtAsync(Position position, CancellationToken cancellationToken = default)
    {
        if (!Sense.IsEditorMode) return null;
        await WaitUntilParsedAsync(cancellationToken);

        position = AdjustPositionForSelectionEnd(position);
        int idx = Sense.Tokens.GetIndex(position);
        return Sense.Tokens.GetAt(idx) is not null ? ParseNamespaceQualifiedIdentifierByIndex(idx) : null;
    }

    /// <summary>
    /// When Go-to-Definition (or a similar navigation request) is triggered while the
    /// editor has a text selection active, the LSP position is the selection end rather
    /// than a point inside the symbol. The selection end often falls exactly on the first
    /// character of the next token — e.g. '(' after a function name, or a space/comma
    /// after a macro name — causing all token and hover lookups to miss.
    ///
    /// This method detects that boundary condition: if the token returned by GetIndex
    /// starts *exactly* at the given position and is not itself an identifier, the cursor
    /// is almost certainly sitting just past an identifier. In that case we step back to
    /// the preceding non-trivia identifier token and return its start position, which
    /// will resolve correctly in both the hover library (exclusive-end ranges) and the
    /// token index lookup.
    /// </summary>
    private Position AdjustPositionForSelectionEnd(Position position)
    {
        int idx = Sense.Tokens.GetIndex(position);
        Token? token = Sense.Tokens.GetAt(idx);

        if (token is not null
            && token.Type != TokenType.Identifier
            && token.TokenRange.StartLine == position.Line
            && token.TokenRange.StartChar == position.Character)
        {
            int prevIdx = Sense.Tokens.PrevNonTriviaIndex(idx);
            Token? prev = Sense.Tokens.GetAt(prevIdx);
            if (prev?.Type == TokenType.Identifier)
            {
                Log.Debug("AdjustPositionForSelectionEnd: cursor at start of '{Tok}' ({Type}), stepping back to identifier '{Id}'",
                    token.Lexeme, token.Type, prev.Lexeme);
                return new Position(prev.TokenRange.StartLine, prev.TokenRange.StartChar);
            }
        }

        return position;
    }

    private Location? TryGetMacroDefinitionLocation(Position position) =>
        Sense.HoverLibrary!.Get(position) switch
        {
            Pre.MacroDefinition { IsBuiltIn: false } macroDef => MakeLocalLocation(macroDef.Range),
            Pre.ScriptMacro scriptMacro                       => MakeLocalLocation(scriptMacro.DefineSource.Range),
            _                                                  => null
        };

    private Location? TryGetSymbolDefinitionLocation(Token token)
    {
        if (Sense.GetSenseDefinition(token) is ScrVariableSymbol varSymbol && varSymbol.DefinitionSource is not null)
        {
            return MakeLocalLocation(varSymbol.DefinitionSource switch
            {
                ExprNode expr             => expr.Range,
                ConstStmtNode constStmt   => constStmt.IdentifierToken.Range,
                ForeachStmtNode           => varSymbol.IdentifierToken.Range,
                MemberDeclNode memberDecl => memberDecl.NameToken?.Range ?? varSymbol.IdentifierToken.Range,
                ParamNode paramNode       => paramNode.Name?.Range ?? varSymbol.IdentifierToken.Range,
                _                         => varSymbol.IdentifierToken.Range
            });
        }

        if (Sense.GetSenseDefinition(token) is ScrParameterSymbol paramSymbol && paramSymbol.DefinitionSource is not null)
        {
            return MakeLocalLocation(paramSymbol.DefinitionSource switch
            {
                ParamNode paramNode => paramNode.Name?.Range ?? paramSymbol.Range,
                _                   => paramSymbol.Range
            });
        }

        return null;
    }

    private Location? TryGetFileReferenceLocation(Position position, Token token, int tokenIdx)
    {
        if (Sense.GetSenseDefinition(token) is ScrDependencySymbol dep && File.Exists(dep.Path))
            return MakeFileStartLocation(dep.Path);

        if (IsOnUsingLineByIndex(tokenIdx, out string? usingPath, out Range? usingRange))
        {
            string? resolved = Sense.GetDependencyPath(usingPath!, usingRange!);
            if (resolved is not null && File.Exists(resolved))
                return MakeFileStartLocation(resolved);
        }

        if (Sense.HoverLibrary!.Get(position) is Pre.InsertDirectiveHover ih)
        {
            string? resolved = Sense.ResolveInsertPath(ih.RawPath, ih.Range);
            if (resolved is not null && File.Exists(resolved))
                return MakeFileStartLocation(resolved);
        }

        return null;
    }

    private Location? TryGetFunctionOrClassLocation(int tokenIdx)
    {
        // Forward namespace qualifier token to the function name token
        if (TryGetQualifiedFunctionTokenByIndex(tokenIdx, out int defFuncIdx))
            tokenIdx = defFuncIdx;

        var tokenSenseDef = Sense.GetSenseDefinition(Sense.Tokens.GetAt(tokenIdx)!);
        bool hasDefinitionSymbol = tokenSenseDef is ScrFunctionSymbol or ScrMethodSymbol
                                                  or ScrClassSymbol or ScrClassReferenceSymbol;

        int nextNonWsIdx = Sense.Tokens.NextNonWhitespaceIndex(tokenIdx);
        bool looksLikeCall = nextNonWsIdx >= 0 && Sense.Tokens.GetAt(nextNonWsIdx)!.Type == TokenType.OpenParen;
        int prevNonTrivIdx = Sense.Tokens.PrevNonTriviaIndex(tokenIdx);
        bool isQualified = prevNonTrivIdx >= 0 && Sense.Tokens.GetAt(prevNonTrivIdx)!.Type == TokenType.ScopeResolution;
        bool isAddressOf = IsAddressOfIdentifierByIndex(tokenIdx);

        Log.Debug("TryGetFunctionOrClassLocation: looksLikeCall={C} isQualified={Q} isAddressOf={A} hasDefinitionSymbol={D}",
            looksLikeCall, isQualified, isAddressOf, hasDefinitionSymbol);

        if (!looksLikeCall && !isQualified && !isAddressOf && !hasDefinitionSymbol) return null;

        var (qualifier, name) = ParseNamespaceQualifiedIdentifierByIndex(tokenIdx);
        if (IsBuiltinFunction(name)) return null;

        string currentScriptPath = ScriptUri.ToUri().LocalPath;
        string ns = GetEffectiveNamespace();

        var loc = (qualifier is not null && DefinitionsTable is not null
                      ? DefinitionsTable.GetFunctionLocation(qualifier, name) ?? DefinitionsTable.GetClassLocation(qualifier, name)
                      : null)
               ?? DefinitionsTable?.GetFunctionLocation(ns, name)
               ?? DefinitionsTable?.GetClassLocation(ns, name)
               ?? DefinitionsTable?.GetFunctionLocationAnyNamespace(name)
               ?? DefinitionsTable?.GetClassLocationAnyNamespace(name);

        return loc is not null
            ? ScriptFileResolver.ResolveDefinitionLocation(currentScriptPath, loc.Value.FilePath, loc.Value.Range.ToRange())
            : null;
    }

    public async Task<SignatureHelp?> GetSignatureHelpAsync(Position position, CancellationToken cancellationToken)
    {
        await WaitUntilParsedAsync(cancellationToken);

        var tokens = Sense.Tokens;
        int tokenIdx = tokens.GetIndex(position);
        Token? token = tokens.GetAt(tokenIdx);
        if (token is null)
            return null;

        // Use index-based call info detection
        if (!TryGetCallInfoByIndex(tokenIdx, out int idIdx, out int activeParam))
            return null;

        var (qualifier, name) = ParseNamespaceQualifiedIdentifierByIndex(idIdx);

        // Try builtin API first
        List<SignatureInformation> signatures = [];
        var api = TryGetApi();
        if (api is not null)
        {
            try
            {
                var apiFn = api.GetApiFunction(name);
                if (apiFn is not null)
                {
                    var docContent = new MarkupContent { Kind = MarkupKind.Markdown, Value = apiFn.Description ?? string.Empty };
                    foreach (var overload in apiFn.Overloads)
                    {
                        IEnumerable<ScrFunctionArg> paramSeq = overload.Parameters;
                        string calledOnStr = overload.CalledOn is ScrFunctionArg co ? $"{co.Name} " : string.Empty;
                        var cleaned = paramSeq.Select(p => StripDefault(p.Name)).ToArray();
                        string label = $"{calledOnStr}function {name}({string.Join(", ", cleaned)})";
                        var parameters = new Container<ParameterInformation>(paramSeq.Select(p => new ParameterInformation { Label = StripDefault(p.Name), Documentation = string.IsNullOrWhiteSpace(p.Description) ? null : new MarkupContent { Kind = MarkupKind.Markdown, Value = p.Description! } }));
                        signatures.Add(new SignatureInformation { Label = label, Documentation = docContent, Parameters = parameters });
                    }
                }
            }
            catch { }
        }

        // Then script-defined (local or imported) using DefinitionsTable
        string ns = qualifier ?? GetEffectiveNamespace();
        string[]? parms = DefinitionsTable?.GetFunctionParameters(ns, name);
        string? doc = DefinitionsTable?.GetFunctionDoc(ns, name);
        if (parms is not null)
        {
            var cleaned = parms.Select(StripDefault).ToArray();
            string label = $"function {name}({string.Join(", ", cleaned)})";
            var paramList = cleaned.Select((p, i) =>
            {
                string? pDoc = ExtractParameterDocFromDoc(doc, p, i);
                return new ParameterInformation
                {
                    Label = p,
                    Documentation = string.IsNullOrWhiteSpace(pDoc) ? null : new MarkupContent { Kind = MarkupKind.Markdown, Value = pDoc }
                };
            }).ToList();
            signatures.Add(new SignatureInformation { Label = label, Parameters = new Container<ParameterInformation>(paramList) });
        }

        if (signatures.Count == 0)
            return null;

        // Find the best matching signature based on parameter count
        int activeSignature = signatures.FindIndex(s => s.Parameters?.Count() > activeParam);
        if (activeSignature < 0) activeSignature = 0;

        int paramCount = signatures[activeSignature].Parameters?.Count() ?? 1;

        return new SignatureHelp
        {
            ActiveSignature = activeSignature,
            ActiveParameter = Math.Max(0, Math.Min(activeParam, paramCount - 1)),
            Signatures = new Container<SignatureInformation>(signatures)
        };
    }

    private static string? ExtractParameterDocFromDoc(string? doc, string paramName, int paramIndex)
    {
        if (string.IsNullOrWhiteSpace(doc) || string.IsNullOrWhiteSpace(paramName)) return null;

        static string Normalize(string s) => s.Trim().Trim('<', '>', '[', ']').Trim();

        // Split once; reuse for both prototype extraction and line scanning.
        string[] lines = doc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Build candidate set: the provided param name + the positionally-matching name
        // from the prototype on the second doc line (```gsc\nfoo(a, b, ...)```).
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(paramName) };
        if (lines.Length > 1)
        {
            string proto = lines[1].Trim();
            int lp = proto.IndexOf('('), rp = proto.LastIndexOf(')');
            if (lp >= 0 && rp > lp)
            {
                var protoParams = proto[(lp + 1)..rp]
                    .Split(',')
                    .Select(Normalize)
                    .Where(p => p.Length > 0)
                    .ToArray();
                if (paramIndex >= 0 && paramIndex < protoParams.Length)
                    candidates.Add(protoParams[paramIndex]);
            }
        }

        // Scan for ``<param>`` — description lines
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int b1 = line.IndexOf('`');
            if (b1 < 0) continue;
            int b2 = line.IndexOf('`', b1 + 1);
            if (b2 < 0) continue;

            if (!candidates.Contains(Normalize(line[(b1 + 1)..b2]))) continue;

            int dash = line.IndexOf('—', b2 + 1);
            if (dash < 0) dash = line.IndexOf('-', b2 + 1);
            return dash >= 0 && dash + 1 < line.Length ? line[(dash + 1)..].Trim() : string.Empty;
        }
        return null;
    }

    private Location MakeLocalLocation(Range range)
    {
        string normalized = ScriptFileResolver.NormalizeFilePathForUri(ScriptUri.ToUri().LocalPath);
        return new Location { Uri = new Uri(normalized), Range = range };
    }

    private static Location MakeFileStartLocation(string filePath)
    {
        string normalized = ScriptFileResolver.NormalizeFilePathForUri(filePath);
        return new Location { Uri = new Uri(normalized), Range = RangeHelper.From(0, 0, 0, 0) };
    }

    private bool TryGetCallInfoByIndex(int tokenIdx, out int idTokenIdx, out int activeParam)
    {
        idTokenIdx = -1;
        activeParam = 0;
        Token? token = Sense.Tokens.GetAt(tokenIdx);
        if (token is null || !TryFindCallContext(token, out Token? idToken, out activeParam) || idToken is null)
            return false;
        idTokenIdx = Sense.Tokens.IndexOf(idToken);
        return idTokenIdx >= 0;
    }
}
