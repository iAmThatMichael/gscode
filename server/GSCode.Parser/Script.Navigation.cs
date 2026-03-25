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

        Log.Debug("GetDefinitionAsync: Starting at position {Position}", position);

        // When Go-to-Definition is triggered with a text selection active the LSP position
        // is the selection end, which may land exactly on the first character of the next
        // token (e.g. '(' after a function name, or a space after a macro). Normalise the
        // position back to the preceding identifier so all subsequent lookups succeed.
        position = AdjustPositionForSelectionEnd(position);

        // First, allow preprocessor macro definitions/usages to resolve even if the original macro token was removed
        IHoverable? hoverable = Sense.HoverLibrary!.Get(position);
        if (hoverable is Pre.MacroDefinition macroDef && !macroDef.IsBuiltIn)
        {
            Log.Debug("GetDefinitionAsync: Found MacroDefinition at position");
            string normalized = ScriptFileResolver.NormalizeFilePathForUri(ScriptUri.ToUri().LocalPath);
            return new Location() { Uri = new Uri(normalized), Range = macroDef.Range };
        }
        if (hoverable is Pre.ScriptMacro scriptMacro)
        {
            Log.Debug("GetDefinitionAsync: Found ScriptMacro at position, navigating to definition");
            string normalized = ScriptFileResolver.NormalizeFilePathForUri(ScriptUri.ToUri().LocalPath);
            return new Location() { Uri = new Uri(normalized), Range = scriptMacro.DefineSource.Range };
        }

        int tokenIdx = Sense.Tokens.GetIndex(position);
        Token? token = Sense.Tokens.GetAt(tokenIdx);
        if (token is null)
        {
            Log.Debug("GetDefinitionAsync: No token found at position {Position}", position);
            return null;
        }

        Log.Debug("GetDefinitionAsync: Found token '{Lexeme}' type={Type} at position {Position}, IsFromPreprocessor={IsPrep}", 
            token.Lexeme, token.Type, position, token.IsFromPreprocessor);

        // Log token range for debugging
        Log.Debug("GetDefinitionAsync: Token range: Start=({Line}:{Char}), End=({EndLine}:{EndChar})", 
            token.Range.Start.Line, token.Range.Start.Character, 
            token.Range.End.Line, token.Range.End.Character);

        // Log previous tokens for context
        Token? prevToken = token.Previous;
        int count = 0;
        while (prevToken != null && count < 5)
        {
            Log.Debug("  Previous token [{Index}]: '{Lexeme}' type={Type}", count, prevToken.Lexeme, prevToken.Type);
            prevToken = prevToken.Previous;
            count++;
        }

        // Check if this is a variable reference - go to its definition
        if (Sense.GetSenseDefinition(token) is ScrVariableSymbol varSymbol && varSymbol.DefinitionSource is not null)
        {
            Log.Debug("GetDefinitionAsync: Token is a variable reference, navigating to definition");
            // Navigate to the definition (the identifier token where it was first declared)
            string normalized = ScriptFileResolver.NormalizeFilePathForUri(ScriptUri.ToUri().LocalPath);
            // Use the Range from the definition source's identifier token (stored in the variable symbol)
            // We need to find the declaration token - for now, use the identifier token from definition source
            Range targetRange = varSymbol.DefinitionSource switch
            {
                ExprNode expr => expr.Range,
                ConstStmtNode constStmt => constStmt.IdentifierToken.Range,
                ForeachStmtNode foreachStmt => varSymbol.IdentifierToken.Range, // Use the identifier token range
                MemberDeclNode memberDecl => memberDecl.NameToken?.Range ?? varSymbol.IdentifierToken.Range,
                ParamNode paramNode => paramNode.Name?.Range ?? varSymbol.IdentifierToken.Range, // Function parameter
                _ => varSymbol.IdentifierToken.Range // Fallback to the identifier token
            };
            return new Location() { Uri = new Uri(normalized), Range = targetRange };
        }

        // Check if this is a parameter reference - go to its definition
        if (Sense.GetSenseDefinition(token) is ScrParameterSymbol paramSymbol && paramSymbol.DefinitionSource is not null)
        {
            Log.Debug("GetDefinitionAsync: Token is a parameter reference, navigating to definition");
            // Navigate to the parameter definition
            string normalized = ScriptFileResolver.NormalizeFilePathForUri(ScriptUri.ToUri().LocalPath);
            // For parameters, use the token from the ParamNode
            Range targetRange = paramSymbol.DefinitionSource switch
            {
                ParamNode paramNode => paramNode.Name?.Range ?? paramSymbol.Range,
                _ => paramSymbol.Range // Fallback to the parameter symbol's range
            };
            return new Location() { Uri = new Uri(normalized), Range = targetRange };
        }

        // If the token has an IntelliSense definition pointing at a dependency, return that file location.
        if (Sense.GetSenseDefinition(token) is ScrDependencySymbol dep)
        {
            Log.Debug("GetDefinitionAsync: Token is a dependency symbol, navigating to file: {Path}", dep.Path);
            string resolvedPath = dep.Path;
            if (!File.Exists(resolvedPath))
            {
                Log.Debug("GetDefinitionAsync: Dependency file does not exist: {Path}", resolvedPath);
                return null;
            }
            string normalized = ScriptFileResolver.NormalizeFilePathForUri(resolvedPath);
            var targetUri = new Uri(normalized);
            // Navigate to start of file in the target document
            return new Location() { Uri = targetUri, Range = RangeHelper.From(0, 0, 0, 0) };
        }

        if (IsOnUsingLineByIndex(tokenIdx, out string? usingPath, out Range? usingRange))
        {
            string? resolved = Sense.GetDependencyPath(usingPath!, usingRange!);
            if (resolved is not null && File.Exists(resolved))
            {
                string normalized = ScriptFileResolver.NormalizeFilePathForUri(resolved);
                var targetUri = new Uri(normalized);
                return new Location() { Uri = targetUri, Range = RangeHelper.From(0, 0, 0, 0) };
            }
        }

        // If on an #insert line and we recorded a hover for it, go to the inserted file
        IHoverable? h = Sense.HoverLibrary.Get(position);
        if (h is Pre.InsertDirectiveHover ih)
        {
            string? resolved = Sense.ResolveInsertPath(ih.RawPath, ih.Range);
            if (resolved is not null && File.Exists(resolved))
            {
                string normalized = ScriptFileResolver.NormalizeFilePathForUri(resolved);
                var targetUri = new Uri(normalized);
                return new Location() { Uri = targetUri, Range = RangeHelper.From(0, 0, 0, 0) };
            }
        }

        // Ensure the token is a function-like identifier before attempting Go-to-Definition.
        if (token.Type != TokenType.Identifier)
        {
            Log.Debug("GetDefinitionAsync: Token is not an identifier, type={Type}", token.Type);
            return null;
        }

        Log.Debug("GetDefinitionAsync: Token is identifier '{Lexeme}', checking heuristics", token.Lexeme);

        // If current token is namespace qualifier (followed by :: and identifier), forward to the function token
        if (TryGetQualifiedFunctionTokenByIndex(tokenIdx, out int defFuncIdx))
        {
            tokenIdx = defFuncIdx;
            token = Sense.Tokens.GetAt(tokenIdx)!;
        }
        int nextNonWsIdx = Sense.Tokens.NextNonWhitespaceIndex(tokenIdx);
        Token? nextNonWs = Sense.Tokens.GetAt(nextNonWsIdx);
        bool looksLikeCall = nextNonWs is not null && nextNonWs.Type == TokenType.OpenParen;
        int prevNonTrivIdx = Sense.Tokens.PrevNonTriviaIndex(tokenIdx);
        Token? prevNonTriv = Sense.Tokens.GetAt(prevNonTrivIdx);
        bool isQualified = prevNonTriv is not null && prevNonTriv.Type == TokenType.ScopeResolution;
        var tokenSenseDef = Sense.GetSenseDefinition(token);
        bool hasDefinitionSymbol = tokenSenseDef is ScrFunctionSymbol || tokenSenseDef is ScrMethodSymbol || tokenSenseDef is ScrClassSymbol || tokenSenseDef is ScrClassReferenceSymbol;
        bool isAddressOf = IsAddressOfIdentifierByIndex(tokenIdx);

        Log.Debug("GetDefinitionAsync: Heuristics - looksLikeCall={Call}, isQualified={Qual}, hasDefinitionSymbol={Def}, isAddressOf={Addr}", 
            looksLikeCall, isQualified, hasDefinitionSymbol, isAddressOf);

        if (!looksLikeCall && !isQualified && !hasDefinitionSymbol && !isAddressOf)
        {
            Log.Debug("GetDefinitionAsync: Failed heuristic checks, returning null");
            return null;
        }

        var (qualifier, name) = ParseNamespaceQualifiedIdentifierByIndex(tokenIdx);
        Log.Debug("GetDefinitionAsync: Parsed identifier - qualifier={Qualifier}, name={Name}", qualifier ?? "(none)", name);

        if (IsBuiltinFunction(name))
        {
            Log.Debug("GetDefinitionAsync: Identifier is builtin function, returning null");
            return null;
        }
        if (qualifier is not null && DefinitionsTable is not null)
        {
            Log.Debug("GetDefinitionAsync: Looking up qualified function/class: {Qualifier}::{Name}", qualifier, name);
            var loc = DefinitionsTable.GetFunctionLocation(qualifier, name)
                   ?? DefinitionsTable.GetClassLocation(qualifier, name);
            if (loc is not null)
            {
                Log.Debug("GetDefinitionAsync: Found qualified definition at {File}:{Range}", loc.Value.FilePath, loc.Value.Range);
                string currentScriptPath = ScriptUri.ToUri().LocalPath;
                return ScriptFileResolver.ResolveDefinitionLocation(currentScriptPath, loc.Value.FilePath, loc.Value.Range.ToRange());
            }
            Log.Debug("GetDefinitionAsync: Qualified lookup failed");
        }
        string ns = GetEffectiveNamespace();
        Log.Debug("GetDefinitionAsync: Looking up in current namespace: {Namespace}", ns);
        var localLoc = DefinitionsTable?.GetFunctionLocation(ns, name)
                    ?? DefinitionsTable?.GetClassLocation(ns, name);
        if (localLoc is not null)
        {
            Log.Debug("GetDefinitionAsync: Found local definition at {File}:{Range}", localLoc.Value.FilePath, localLoc.Value.Range);
            string currentScriptPath = ScriptUri.ToUri().LocalPath;
            return ScriptFileResolver.ResolveDefinitionLocation(currentScriptPath, localLoc.Value.FilePath, localLoc.Value.Range.ToRange());
        }
        Log.Debug("GetDefinitionAsync: Local namespace lookup failed, trying any namespace");
        var anyLoc = DefinitionsTable?.GetFunctionLocationAnyNamespace(name)
                  ?? DefinitionsTable?.GetClassLocationAnyNamespace(name);
        if (anyLoc is not null)
        {
            Log.Debug("GetDefinitionAsync: Found definition in any namespace at {File}:{Range}", anyLoc.Value.FilePath, anyLoc.Value.Range);
            string currentScriptPath = ScriptUri.ToUri().LocalPath;
            return ScriptFileResolver.ResolveDefinitionLocation(currentScriptPath, anyLoc.Value.FilePath, anyLoc.Value.Range.ToRange());
        }
        Log.Debug("GetDefinitionAsync: All lookups failed, returning null");
        return null;
    }

    public async Task<(string? qualifier, string name)?> GetQualifiedIdentifierAtAsync(Position position, CancellationToken cancellationToken = default)
    {
        if (!Sense.IsEditorMode) return null;
        await WaitUntilParsedAsync(cancellationToken);

        position = AdjustPositionForSelectionEnd(position);

        int idx = Sense.Tokens.GetIndex(position);
        Token? token = Sense.Tokens.GetAt(idx);
        if (token is null)
        {
            return null;
        }

        return ParseNamespaceQualifiedIdentifierByIndex(idx);
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

        Token? id = tokens.GetAt(idIdx);
        if (id is null)
            return null;

        var (qualifier, name) = ParseNamespaceQualifiedIdentifierByIndex(idIdx);

        // Try builtin API first
        List<SignatureInformation> signatures = new();
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
        string[]? parms = DefinitionsTable?.GetFunctionParameters(ns, name) ?? DefinitionsTable?.GetFunctionParameters(qualifier ?? ns, name);
        string? doc = DefinitionsTable?.GetFunctionDoc(ns, name);
        if (parms is not null)
        {
            var cleaned = parms.Select(StripDefault).ToArray();
            string label = $"function {name}({string.Join(", ", cleaned)})";
            var paramList = new List<ParameterInformation>(cleaned.Length);
            for (int i = 0; i < cleaned.Length; i++)
            {
                string p = cleaned[i];
                string? pDoc = ExtractParameterDocFromDoc(doc, p, i);
                paramList.Add(new ParameterInformation
                {
                    Label = p,
                    Documentation = string.IsNullOrWhiteSpace(pDoc) ? null : new MarkupContent { Kind = MarkupKind.Markdown, Value = pDoc }
                });
            }
            var parameters = new Container<ParameterInformation>(paramList);
            // Do not include full Markdown doc in SignatureHelp for script-defined; keep prototype and parameters only
            signatures.Add(new SignatureInformation { Label = label, Parameters = parameters });
        }

        if (signatures.Count == 0)
            return null;

        // Find the best matching signature based on parameter count
        int activeSignature = 0;
        for (int i = 0; i < signatures.Count; i++)
        {
            if (signatures[i].Parameters is { } p && p.Count() > activeParam)
            {
                activeSignature = i;
                break;
            }
        }

        int paramCount = 1;
        if (signatures[activeSignature].Parameters is { } paramContainer)
        {
            paramCount = paramContainer.Count();
        }

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
        string Normalize(string s) => s.Trim().Trim('<', '>', '[', ']').Trim();

        // Try to parse prototype line to map by position
        string[] ParseDocPrototypeParams(string d)
        {
            var lines = d.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var line = lines.Length > 1 ? lines[1].Trim() : string.Empty; // second line from ```gsc\r\nfoo(bar)...```
            int lp = line.IndexOf('(');
            int rp = line.LastIndexOf(')');
            if (lp < 0 || rp < lp) return Array.Empty<string>();
            string inside = line.Substring(lp + 1, rp - lp - 1);
            if (string.IsNullOrWhiteSpace(inside)) return Array.Empty<string>();
            var parts = inside.Split(',');
            var list = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var v = Normalize(p);
                if (v.Length > 0) list.Add(v);
            }
            return list.ToArray();
        }

        string[] protoParams = ParseDocPrototypeParams(doc);
        List<string> candidates = new() { Normalize(paramName) };
        if (paramIndex >= 0 && paramIndex < protoParams.Length)
        {
            candidates.Add(Normalize(protoParams[paramIndex]));
        }

        // Scan parameter description lines (``<param>`` — desc)
        string[] linesDoc = doc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in linesDoc)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int b1 = line.IndexOf('`');
            if (b1 < 0) continue;
            int b2 = line.IndexOf('`', b1 + 1);
            if (b2 < 0) continue;
            string token = Normalize(line.Substring(b1 + 1, b2 - b1 - 1));
            bool match = false;
            foreach (var c in candidates)
            {
                if (string.Equals(c, token, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
            }
            if (!match) continue;
            int dash = line.IndexOf('—', b2 + 1); // em dash
            if (dash < 0) dash = line.IndexOf('-', b2 + 1); // fallback
            string desc = dash >= 0 && dash + 1 < line.Length ? line[(dash + 1)..].Trim() : string.Empty;
            return desc;
        }
        return null;
    }

    private bool TryGetCallInfoByIndex(int tokenIdx, out int idTokenIdx, out int activeParam)
    {
        idTokenIdx = -1;
        activeParam = 0;
        Token? token = Sense.Tokens.GetAt(tokenIdx);
        if (token is null) return false;
        if (!TryFindCallContext(token, out Token? idToken, out activeParam)) return false;
        if (idToken is null) return false;
        idTokenIdx = Sense.Tokens.IndexOf(idToken);
        return idTokenIdx >= 0;
    }
}
