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

        // Try macro lookup at the raw (unadjusted) position first. Macro call tokens are consumed
        // by the preprocessor so they don't appear in the token list; their ScriptMacro hover entry
        // is keyed by the original source range. AdjustPositionForSelectionEnd can shift away from
        // that range when the cursor lands on an expanded token that shares the macro name's position.
        if (TryGetMacroDefinitionLocation(position) is { } macroLocRaw)
            return macroLocRaw;

        // When Go-to-Definition is triggered with a text selection active the LSP position
        // is the selection end, which may land exactly on the first character of the next
        // token (e.g. '(' after a function name, or a space after a macro). Normalise the
        // position back to the preceding identifier so all subsequent lookups succeed.
        position = AdjustPositionForSelectionEnd(position);

        if (TryGetMacroDefinitionLocation(position) is { } macroLoc)
            return macroLoc;

        int tokenIdx = Sense.Tokens.GetIndex(position);
        Token? token = Sense.Tokens.GetAt(tokenIdx);

        // Check for #insert go-to-definition before the null-token guard — #insert path tokens
        // are preprocessor-level and never appear in Sense.Tokens, so token may be null here.
        if (Sense.HoverLibrary!.Get(position) is Pre.InsertDirectiveHover ih)
        {
            string? resolved = Sense.ResolveInsertPath(ih.RawPath, ih.Range);
            if (resolved is not null && File.Exists(resolved))
                return MakeFileStartLocation(resolved);
        }

        if (token is null) return null;

        // Skip preprocessor-expanded tokens — they carry the macro name's source range, not the
        // real call-site, so symbol lookups on them produce wrong results.
        if (token.IsFromPreprocessor) return null;

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
        Token? token = Sense.Tokens.GetAt(idx);
        if (token is null || token.Type != TokenType.Identifier) return null;
        return ParseNamespaceQualifiedIdentifierByIndex(idx);
    }

    /// <summary>
    /// Returns the identifier token name and its source range at the given cursor position,
    /// or <see langword="null"/> if the cursor is not over a renameable identifier.
    /// Preprocessor-expanded tokens and non-identifier tokens are rejected.
    /// </summary>
    public async Task<(string Name, Range Range)?> GetRenameTargetAtAsync(Position position, CancellationToken cancellationToken = default)
    {
        if (!Sense.IsEditorMode) return null;
        await WaitUntilParsedAsync(cancellationToken);

        position = AdjustPositionForSelectionEnd(position);
        int idx = Sense.Tokens.GetIndex(position);
        Token? token = Sense.Tokens.GetAt(idx);
        if (token is null || token.Type != TokenType.Identifier) return null;
        if (token.IsFromPreprocessor) return null;
        return (token.Lexeme, token.Range);
    }

    /// <summary>
    /// If the cursor is on the field part of a dot-access expression (e.g. <c>foo</c> in <c>obj.foo</c>),
    /// returns the lowered field name and its token range. Returns null otherwise.
    /// </summary>
    public async Task<(string Field, Range FieldRange)?> GetGlobalFieldAtAsync(Position position, CancellationToken cancellationToken = default)
    {
        if (!Sense.IsEditorMode) return null;
        await WaitUntilParsedAsync(cancellationToken);

        position = AdjustPositionForSelectionEnd(position);
        var tokens = Sense.Tokens;
        int idx = tokens.GetIndex(position);
        Token? token = tokens.GetAt(idx);
        if (token is null || token.Type != TokenType.Identifier) return null;

        // Cursor must be on the field identifier (right of dot)
        int prevIdx = tokens.PrevNonTriviaIndex(idx);
        if (prevIdx < 0 || tokens.GetAt(prevIdx)!.Type != TokenType.Dot) return null;

        // Exclude method calls — those are indexed as Function refs, not Field refs
        int nextIdx = tokens.NextNonWhitespaceIndex(idx);
        if (nextIdx >= 0 && tokens.GetAt(nextIdx)!.Type == TokenType.OpenParen) return null;

        return (token.Lexeme.ToLowerInvariant(), token.Range);
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
                return new Position { Line = prev.TokenRange.StartLine, Character = prev.TokenRange.StartChar };
            }
        }

        return position;
    }

    private Location? TryGetMacroDefinitionLocation(Position position) =>
        Sense.HoverLibrary!.Get(position) switch
        {
            Pre.MacroDefinition { IsBuiltIn: false } macroDef =>
                macroDef.SourceFilePath is string srcPath && File.Exists(srcPath)
                    ? MakeFileLocation(srcPath, macroDef.Range)
                    : MakeLocalLocation(macroDef.Range),
            Pre.ScriptMacro scriptMacro =>
                scriptMacro.DefineSource.SourceFilePath is string srcPath && File.Exists(srcPath)
                    ? MakeFileLocation(srcPath, scriptMacro.DefineSource.Range)
                    : MakeLocalLocation(scriptMacro.DefineSource.Range),
            _ => null
        };

    private static Location MakeFileLocation(string filePath, Range range)
    {
        string normalized = ScriptFileResolver.NormalizeFilePathForUri(filePath);
        return new Location { Uri = new Uri(normalized), Range = range };
    }

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
            string? resolved = Sense.ResolveUsingPath(usingPath!, usingRange!);
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

        string currentScriptPath = ScriptUri.LocalPath;
        string ns = GetEffectiveNamespace();

        // When the call site carries an explicit namespace qualifier (e.g. util::init),
        // only look inside that namespace.  Falling through to the AnyNamespace helpers
        // would silently resolve to an unrelated function that merely shares the same
        // name in a different namespace, causing GoTo to jump to the wrong file.
        (string FilePath, TokenRange Range)? loc;
        if (qualifier is not null && DefinitionsTable is not null)
        {
            loc = DefinitionsTable.GetFunctionLocation(qualifier, name)
               ?? DefinitionsTable.GetClassLocation(qualifier, name);
        }
        else
        {
            loc = DefinitionsTable?.GetFunctionLocation(ns, name)
               ?? DefinitionsTable?.GetClassLocation(ns, name);

            if (loc is null)
            {
                // Any-namespace lookup may resolve via the workspace-wide global provider.
                // Only accept the result if the symbol is defined in this file or in an
                // explicitly #using-imported dependency — otherwise the function is out of
                // scope and should not navigate.
                var anyLoc = DefinitionsTable?.GetFunctionLocationAnyNamespace(name)
                          ?? DefinitionsTable?.GetClassLocationAnyNamespace(name);

                if (anyLoc is not null && IsInScope(anyLoc.Value.FilePath))
                    loc = anyLoc;
            }
        }

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

        if (!TryGetCallInfoByIndex(tokenIdx, out int idIdx, out int activeParam))
            return null;

        Token? idToken = tokens.GetAt(idIdx);
        var (qualifier, name) = ParseNamespaceQualifiedIdentifierByIndex(idIdx);

        // Resolve ScrFunction via the same chain as hover: sense definition → global provider → API
        ScrFunction? function = Sense.GetSenseDefinition(idToken) switch
        {
            ScrMethodSymbol ms            => ms.Source,
            ScrFunctionSymbol fs          => fs.Source,
            ScrFunctionReferenceSymbol rs => rs.Source,
            _                             => null
        };

        string ns = qualifier ?? GetEffectiveNamespace();
        function ??= GlobalSymbolProvider?.GetFunction(ns, name)
                  ?? TryGetApi()?.GetApiFunction(name);

        if (function is null)
            return null;

        string? doc = function.DocComment;
        List<SignatureInformation> signatures = BuildSignatures(function, name, doc);

        if (signatures.Count == 0)
            return null;

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

    private static List<SignatureInformation> BuildSignatures(ScrFunction function, string name, string? doc)
    {
        List<SignatureInformation> signatures = [];

        StringOrMarkupContent? funcDoc = string.IsNullOrWhiteSpace(function.Description)
            ? null
            : new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = function.Description });

        foreach (var overload in function.Overloads)
        {
            string calledOnStr = overload.CalledOn is ScrFunctionArg co ? $"{co.Name} " : string.Empty;
            string keyword = function.IsBuiltIn ? string.Empty : "function ";

            var paramNames = overload.Parameters.Select(p => StripDefault(p.Name)).ToArray();
            string label = $"{calledOnStr}{keyword}{name}({string.Join(", ", paramNames)})";

            var paramInfos = overload.Parameters.Select((p, i) =>
            {
                string pName = StripDefault(p.Name);
                string? pDoc = string.IsNullOrWhiteSpace(p.Description)
                    ? ExtractParameterDocFromDoc(doc, pName, i)
                    : p.Description;
                return new ParameterInformation
                {
                    Label = pName,
                    Documentation = string.IsNullOrWhiteSpace(pDoc)
                        ? null
                        : new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = pDoc })
                };
            });

            signatures.Add(new SignatureInformation
            {
                Label = label,
                Documentation = funcDoc,
                Parameters = new Container<ParameterInformation>(paramInfos)
            });
        }

        return signatures;
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

    /// <summary>
    /// Returns true if <paramref name="filePath"/> is this script or one of its declared
    /// <c>#using</c> dependencies. Used to prevent workspace-wide GoTo Definition from
    /// resolving symbols that are not in scope for this file.
    /// </summary>
    private bool IsInScope(string filePath)
    {
        if (string.Equals(filePath, ScriptUri.LocalPath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (DefinitionsTable is null)
            return false;

        foreach (Uri dep in DefinitionsTable.UsingPaths)
        {
            if (string.Equals(dep.LocalPath, filePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private Location MakeLocalLocation(Range range)
    {
        string normalized = ScriptFileResolver.NormalizeFilePathForUri(ScriptUri.LocalPath);
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
