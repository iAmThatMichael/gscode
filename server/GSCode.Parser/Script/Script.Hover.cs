using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;

namespace GSCode.Parser;

public partial class Script
{
    public async Task<Hover?> GetHoverAsync(Position position, CancellationToken cancellationToken)
    {
        if (!Sense.IsEditorMode) return null;
        await WaitUntilAnalysedAsync(cancellationToken);

        // Priority 0: HoverLibrary covers macros, directives, and other non-token hoverables
        IHoverable? hoverable = Sense.HoverLibrary!.Get(position);
        if (hoverable is not null)
            return hoverable.GetHover();

        Token? token = Sense.Tokens.Get(position);
        if (token is null) return null;

        // Priority 0.5: If the cursor is on the owner of a dot-field (e.g. "level" in "level.zombie_spawners"),
        // suppress further lookup — the owner itself isn't a function and would fall through to a bad hover.
        if (token.Type == TokenType.Identifier && GlobalObjectOwners.All.Contains(token.Lexeme))
        {
            int ownerIdx = Sense.Tokens.IndexOf(token);
            int nextIdx = Sense.Tokens.NextNonWhitespaceIndex(ownerIdx);
            if (nextIdx >= 0 && Sense.Tokens.GetAt(nextIdx)!.Type == TokenType.Dot)
                return null;
        }

        // Priority 1: Inside a function call's arguments — show documentation with active param highlighted.
        // Skip if hovering the function name itself (that falls through to Priority 2/3).
        if (TryGetCallInfo(token, out Token? callIdToken, out int activeParam) && token != callIdToken)
            return BuildCallSignatureHover(callIdToken, activeParam);

        // Priority 2: Namespace qualifier (e.g. "util" in "util::foo") — forward to the function token
        if (token.Type == TokenType.Identifier && TryGetQualifiedFunctionToken(token, out Token? functionToken))
            token = functionToken;

        // Priority 3: Token-based hover from HoverLibrary (covers sense definitions registered during analysis)
        hoverable = Sense.HoverLibrary.Get(token!.Range.Start);
        if (hoverable is not null)
            return hoverable.GetHover();

        // Priority 4: No sense definition — try resolving from the global provider or API
        if (token.Type == TokenType.Identifier)
            return TryBuildFallbackFunctionHover(token);

        return null;
    }

    private static bool TryGetQualifiedFunctionToken(Token token, out Token? functionToken)
    {
        Token? next = token.NextNonTrivia();
        if (next?.Type != TokenType.ScopeResolution)
        {
            functionToken = null;
            return false;
        }
        functionToken = next.NextNonTrivia();
        return functionToken?.Type == TokenType.Identifier;
    }

    private Hover? BuildCallSignatureHover(Token? functionToken, int activeParam)
    {
        if (functionToken is null) return null;

        var (qualifier, funcName) = ParseNamespaceQualifiedIdentifierByIndex(Sense.Tokens.IndexOf(functionToken));
        ScrFunction? function = ResolveFunction(functionToken, qualifier, funcName);
        if (function is null) return null;

        return MakeHover(functionToken.Range, function.GetDocumentationWithActiveParam(activeParam));
    }

    private Hover? TryBuildFallbackFunctionHover(Token token)
    {
        int tokenIdx = Sense.Tokens.IndexOf(token);
        var (qualifier, name) = tokenIdx >= 0
            ? ParseNamespaceQualifiedIdentifierByIndex(tokenIdx)
            : (null, token.Lexeme);

        ScrFunction? function = ResolveFunction(null, qualifier, name);
        if (function is null) return null;

        return MakeHover(token.Range, function.Documentation);
    }

    /// <summary>
    /// Resolves a <see cref="ScrFunction"/> for the given identifier via the shared chain:
    /// sense definition on <paramref name="functionToken"/> (if any) → global symbol provider → built-in API.
    /// Shared by hover, call-signature hover, and signature-help lookups.
    /// </summary>
    private ScrFunction? ResolveFunction(Token? functionToken, string? qualifier, string name)
    {
        ScrFunction? function = functionToken is not null
            ? Sense.GetSenseDefinition(functionToken) switch
            {
                ScrMethodSymbol ms            => ms.Source,  // before ScrFunctionSymbol — it's a subtype
                ScrFunctionSymbol fs          => fs.Source,
                ScrFunctionReferenceSymbol rs => rs.Source,
                _                             => null
            }
            : null;

        string ns = qualifier ?? GetEffectiveNamespace();
        return function
            ?? GlobalSymbolProvider?.GetFunction(ns, name)
            ?? TryGetApi()?.GetApiFunction(name);
    }

    private static Hover MakeHover(Range range, string markdown) => new()
    {
        Range = range,
        Contents = new MarkedStringsOrMarkupContent(new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = markdown
        })
    };
}
