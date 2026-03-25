using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser;

using SymbolKindSA = GSCode.Parser.SA.SymbolKind;

public partial class Script
{
    private void BuildReferenceIndex()
    {
        _references.Clear();
        var api = TryGetApi();
        var tokens = Sense.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens.GetAt(i)!;
            if (token.Type != TokenType.Identifier) continue;

            // Recognize definition identifiers
            var senseDef = Sense.GetSenseDefinition(token);
            if (senseDef is ScrFunctionSymbol)
            {
                var defNamespace = GetEffectiveNamespace();
                AddRef(new SymbolKey(SymbolKindSA.Function, defNamespace, token.Lexeme), token.Range);
                continue;
            }
            if (senseDef is ScrClassSymbol)
            {
                var defNamespace = GetEffectiveNamespace();
                AddRef(new SymbolKey(SymbolKindSA.Class, defNamespace, token.Lexeme), token.Range);
                continue;
            }

            // Recognize call-site or qualified references, or address-of '&name' / '&ns::name'
            int nextIdx = tokens.NextNonWhitespaceIndex(i);
            bool looksLikeCall = nextIdx >= 0 && tokens.GetAt(nextIdx)!.Type == TokenType.OpenParen;
            int prevIdx = tokens.PrevNonTriviaIndex(i);
            bool isQualified = prevIdx >= 0 && tokens.GetAt(prevIdx)!.Type == TokenType.ScopeResolution
                && tokens.PrevNonTriviaIndex(prevIdx) is int nsPrevIdx && nsPrevIdx >= 0
                && tokens.GetAt(nsPrevIdx)!.Type == TokenType.Identifier;
            bool isAddressOf = IsAddressOfIdentifierByIndex(i);
            if (!looksLikeCall && !isQualified && !isAddressOf) continue;

            var (qual, name) = ParseNamespaceQualifiedIdentifierByIndex(i);

            // Skip builtin
            if (api is not null)
            {
                try { if (api.GetApiFunction(name) is not null) continue; } catch { }
            }

            // Resolve to a namespace
            string resolvedNamespace = qual ?? GetEffectiveNamespace();
            // Index as function reference for now (method support can be added later)
            AddRef(new SymbolKey(SymbolKindSA.Function, resolvedNamespace, name), token.Range);
        }

        void AddRef(SymbolKey key, Range range)
        {
            if (!_references.TryGetValue(key, out var list))
            {
                list = new List<Range>();
                _references[key] = list;
            }
            list.Add(range);
        }
    }

    /// <summary>
    /// Precomputes function scope info from the AST before it's disposed.
    /// </summary>
    private void PrecomputeFunctionScopes()
    {
        if (RootNode is null) return;
        _functionScopes = new List<FunctionScopeInfo>();
        foreach (var fn in EnumerateFunctions(RootNode))
        {
            string? name = fn.Name?.Lexeme;
            Range bodyRange = GetStmtListRange(fn.Body);
            var parameters = new List<(string Name, Range Range)>();
            foreach (var p in fn.Parameters.Parameters)
            {
                if (p.Name is not null)
                {
                    parameters.Add((p.Name.Lexeme, p.Name.Range));
                }
            }
            _functionScopes.Add(new FunctionScopeInfo(name, bodyRange, parameters));
        }
    }

    public async Task<string?> GetEnclosingFunctionScopeIdAsync(Position position, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        // Use precomputed scopes if available (post-analysis, AST disposed)
        if (_functionScopes is not null)
        {
            foreach (var scope in _functionScopes)
            {
                if (scope.FunctionName is null) continue;
                if (IsPositionInsideRange(position, scope.BodyRange))
                {
                    return $"{GetEffectiveNamespace()}::{scope.FunctionName}";
                }
            }
            return null;
        }

        // Fallback: use AST directly (pre-analysis)
        if (RootNode is null) return null;
        foreach (var fn in EnumerateFunctions(RootNode))
        {
            if (fn.Name is not Token nameTok) continue;
            var bodyRange = GetStmtListRange(fn.Body);
            if (IsPositionInsideRange(position, bodyRange))
            {
                return $"{GetEffectiveNamespace()}::{nameTok.Lexeme}";
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<Range>> GetLocalVariableReferencesAsync(Position position, bool includeDeclaration, CancellationToken cancellationToken = default)
    {
        if (!Sense.IsEditorMode) return Array.Empty<Range>();
        await WaitUntilParsedAsync(cancellationToken);

        // Acquire the token under the cursor
        Token? token = Sense.Tokens.Get(position);
        if (token is null || token.Type != TokenType.Identifier)
        {
            return Array.Empty<Range>();
        }
        string target = token.Lexeme;

        // Find the enclosing function scope
        FunctionScopeInfo? enclosingScope = null;

        if (_functionScopes is not null)
        {
            // Post-analysis: use precomputed scopes
            foreach (var scope in _functionScopes)
            {
                if (IsPositionInsideRange(position, scope.BodyRange))
                {
                    enclosingScope = scope;
                    break;
                }
                foreach (var (pName, pRange) in scope.Parameters)
                {
                    if (ComparePosition(position, pRange.Start) >= 0 && ComparePosition(pRange.End, position) >= 0)
                    {
                        enclosingScope = scope;
                        break;
                    }
                }
                if (enclosingScope is not null) break;
            }
        }
        else if (RootNode is not null)
        {
            // Pre-analysis fallback: compute from AST
            foreach (var fn in EnumerateFunctions(RootNode))
            {
                var bodyRange = GetStmtListRange(fn.Body);
                if (IsPositionInsideRange(position, bodyRange))
                {
                    var parms = new List<(string Name, Range Range)>();
                    foreach (var p in fn.Parameters.Parameters)
                    {
                        if (p.Name is not null) parms.Add((p.Name.Lexeme, p.Name.Range));
                    }
                    enclosingScope = new FunctionScopeInfo(fn.Name?.Lexeme, bodyRange, parms);
                    break;
                }
                foreach (var p in fn.Parameters.Parameters)
                {
                    if (p.Name is null) continue;
                    if (ComparePosition(position, p.Name.Range.Start) >= 0 && ComparePosition(p.Name.Range.End, position) >= 0)
                    {
                        var parms = new List<(string Name, Range Range)>();
                        foreach (var pp in fn.Parameters.Parameters)
                        {
                            if (pp.Name is not null) parms.Add((pp.Name.Lexeme, pp.Name.Range));
                        }
                        enclosingScope = new FunctionScopeInfo(fn.Name?.Lexeme, GetStmtListRange(fn.Body), parms);
                        break;
                    }
                }
                if (enclosingScope is not null) break;
            }
        }

        if (enclosingScope is null)
        {
            return Array.Empty<Range>();
        }

        // Collect identifier tokens within function body matching the name
        Range body = enclosingScope.BodyRange;
        var results = new List<Range>();
        var tokensLib = Sense.Tokens;
        for (int i = 0; i < tokensLib.Count; i++)
        {
            Token t = tokensLib.GetAt(i)!;
            if (t.Type != TokenType.Identifier) continue;
            // Restrict to tokens in the same function body
            if (!(ComparePosition(t.Range.Start, body.Start) >= 0 && ComparePosition(body.End, t.Range.End) >= 0))
                continue;
            // Match name (case-insensitive to be consistent with SPA checks)
            if (!string.Equals(t.Lexeme, target, StringComparison.OrdinalIgnoreCase)) continue;
            // Exclude namespace-qualified identifiers or function calls
            int prevIdx = tokensLib.PrevNonTriviaIndex(i);
            if (prevIdx >= 0 && tokensLib.GetAt(prevIdx)!.Type == TokenType.ScopeResolution) continue;
            int nextIdx = tokensLib.NextNonWhitespaceIndex(i);
            if (nextIdx >= 0 && tokensLib.GetAt(nextIdx)!.Type == TokenType.OpenParen) continue;

            results.Add(t.Range);
        }

        // Optionally include declaration site (parameter declaration)
        if (includeDeclaration)
        {
            foreach (var (pName, pRange) in enclosingScope.Parameters)
            {
                if (string.Equals(pName, target, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(pRange);
                }
            }
        }

        return results;
    }
}
