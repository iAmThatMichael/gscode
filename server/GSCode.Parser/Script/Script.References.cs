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
        var tokens = Sense.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens.GetAt(i)!;
            if (token.Type != TokenType.Identifier) continue;

            // Recognize definition identifiers (populated by SignatureAnalyser before this runs)
            var senseDef = Sense.GetSenseDefinition(token);
            if (senseDef is ScrFunctionSymbol or ScrClassSymbol)
            {
                var kind = senseDef is ScrFunctionSymbol ? SymbolKindSA.Function : SymbolKindSA.Class;
                AddRef(new SymbolKey(kind, GetEffectiveNamespace(), token.Lexeme.ToLowerInvariant()), token.Range);
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
            if (IsBuiltinFunction(name)) continue;

            // Resolve to a namespace
            string resolvedNamespace = (qual ?? GetEffectiveNamespace()).ToLowerInvariant();
            // Index as function reference for now (method support can be added later)
            AddRef(new SymbolKey(SymbolKindSA.Function, resolvedNamespace, name.ToLowerInvariant()), token.Range);
        }

        // Second pass: index dot-field accesses (.foo) as Field symbols keyed by name only.
        // Owner is intentionally excluded — GSC is dynamically typed and any variable can alias
        // any object (e.g. x = level; x.foo is the same field as level.foo).
        // This must be a separate token-scan pass because ScrFieldSymbol sense tokens are not yet
        // available here — they are populated by the DFA which runs after BuildReferenceIndex.
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens.GetAt(i)!;
            if (token.Type != TokenType.Dot) continue;

            int nextIdx = tokens.NextNonWhitespaceIndex(i);
            if (nextIdx < 0) continue;
            Token? fieldToken = tokens.GetAt(nextIdx);
            if (fieldToken is null || fieldToken.Type != TokenType.Identifier) continue;

            // Exclude call-sites (obj.Method() — these are indexed as Function refs)
            int afterFieldIdx = tokens.NextNonWhitespaceIndex(nextIdx);
            if (afterFieldIdx >= 0 && tokens.GetAt(afterFieldIdx)!.Type == TokenType.OpenParen) continue;

            AddRef(new SymbolKey(SymbolKindSA.Field, "", fieldToken.Lexeme.ToLowerInvariant()), fieldToken.Range);
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
        _functionScopes = EnumerateFunctions(RootNode)
            .Select(fn => new FunctionScopeInfo(
                fn.Name?.Lexeme,
                GetStmtListRange(fn.Body),
                fn.Parameters.Parameters
                    .Where(p => p.Name is not null)
                    .Select(p => (p.Name!.Lexeme, p.Name.Range))
                    .ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<Range>> GetLocalVariableReferencesAsync(Position position, bool includeDeclaration, CancellationToken cancellationToken = default)
    {
        if (!Sense.IsEditorMode) return [];
        await WaitUntilParsedAsync(cancellationToken);

        // Acquire the token under the cursor
        Token? token = Sense.Tokens.Get(position);
        if (token is null || token.Type != TokenType.Identifier)
            return [];
        string target = token.Lexeme;

        // Find the enclosing function scope
        FunctionScopeInfo? enclosingScope = null;

        static List<(string Name, Range Range)> GetParamRanges(FunDefnNode fn) =>
            fn.Parameters.Parameters
                .Where(p => p.Name is not null)
                .Select(p => (p.Name!.Lexeme, p.Name.Range))
                .ToList();

        if (_functionScopes is not null)
        {
            // Post-analysis: use precomputed scopes
            enclosingScope = _functionScopes.FirstOrDefault(scope =>
                IsPositionInsideRange(position, scope.BodyRange) ||
                scope.Parameters.Any(p =>
                    ComparePosition(position, p.Range.Start) >= 0 &&
                    ComparePosition(p.Range.End, position) >= 0));
        }
        else if (RootNode is not null)
        {
            // Pre-analysis fallback: compute from AST
            foreach (var fn in EnumerateFunctions(RootNode))
            {
                var bodyRange = GetStmtListRange(fn.Body);
                if (IsPositionInsideRange(position, bodyRange))
                {
                    enclosingScope = new FunctionScopeInfo(fn.Name?.Lexeme, bodyRange, GetParamRanges(fn));
                    break;
                }
                foreach (var p in fn.Parameters.Parameters)
                {
                    if (p.Name is null) continue;
                    if (ComparePosition(position, p.Name.Range.Start) >= 0 && ComparePosition(p.Name.Range.End, position) >= 0)
                    {
                        enclosingScope = new FunctionScopeInfo(fn.Name?.Lexeme, bodyRange, GetParamRanges(fn));
                        break;
                    }
                }
                if (enclosingScope is not null) break;
            }
        }

        if (enclosingScope is null) return [];

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
            // Exclude namespace-qualified identifiers, dot-field accesses, or function calls
            int prevIdx = tokensLib.PrevNonTriviaIndex(i);
            if (prevIdx >= 0 && tokensLib.GetAt(prevIdx)!.Type is TokenType.ScopeResolution or TokenType.Dot) continue;
            int nextIdx = tokensLib.NextNonWhitespaceIndex(i);
            if (nextIdx >= 0 && tokensLib.GetAt(nextIdx)!.Type == TokenType.OpenParen) continue;

            results.Add(t.Range);
        }

        // Optionally include declaration site (parameter declaration)
        if (includeDeclaration)
        {
            results.AddRange(enclosingScope.Parameters
                .Where(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Range));
        }

        return results;
    }
}
