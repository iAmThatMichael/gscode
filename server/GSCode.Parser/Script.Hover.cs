using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using System.Linq;
using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;

namespace GSCode.Parser;

public partial class Script
{
    public async Task<Hover?> GetHoverAsync(Position position, CancellationToken cancellationToken)
    {
        if (!Sense.IsEditorMode) return null;
        await WaitUntilAnalysedAsync(cancellationToken);

        // Check if position is on a namespace qualifier (followed by :: and identifier)
        // If so, forward to the function token for hover resolution
        int initialIdx = Sense.Tokens.GetIndex(position);
        Token? initialToken = Sense.Tokens.GetAt(initialIdx);
        if (initialToken is not null && initialToken.Type == TokenType.Identifier)
        {
            if (TryGetQualifiedFunctionTokenByIndex(initialIdx, out int funcIdx))
            {
                // Forward position to the function token for hover lookup
                position = Sense.Tokens.GetAt(funcIdx)!.Range.Start;
            }
        }

        // Priority 0: Check HoverLibrary first for special cases (preprocessor macros, directives, etc.)
        // These might not have regular tokens but still need hover support
        IHoverable? hoverable = Sense.HoverLibrary!.Get(position);
        if (hoverable is not null)
        {
            Log.Debug("[HOVER] Priority 0: Returning hoverable from library at position {Pos}", position);
            return hoverable.GetHover();
        }

        Token? token = Sense.Tokens.Get(position);
        if (token is null)
        {
            Log.Debug("[HOVER] No token found at position {Pos}", position);
            return null;
        }

        Log.Debug("[HOVER] Found token: Type={Type}, Lexeme='{Lexeme}' at position {Pos}", 
            token.Type, token.Lexeme, position);

        // Priority 1: If inside a function call's parentheses, show signature with highlighted parameter
        // But NOT if we're hovering on the function name itself - that should show the normal hover
        if (TryGetCallInfo(token, out Token? callIdToken, out int activeParam))
        {
            // Check if we're hovering on the function identifier itself (not inside parentheses)
            bool isOnFunctionIdentifier = token == callIdToken;

            if (!isOnFunctionIdentifier)
            {
                Log.Debug("[HOVER] Priority 1: Inside function call, building signature hover for '{Func}' with active param {Param}", 
                    callIdToken?.Lexeme ?? "unknown", activeParam);
                return BuildCallSignatureHover(callIdToken, activeParam);
            }
            else
            {
                Log.Debug("[HOVER] Priority 1: On function identifier itself, skipping signature hover");
            }
        }

        // Priority 2: If hovering on namespace qualifier, forward to the actual function
        if (token.Type == TokenType.Identifier && TryGetQualifiedFunctionToken(token, out Token? functionToken))
        {
            Log.Debug("[HOVER] Priority 2: Namespace qualifier detected, forwarding to function token");
            token = functionToken;
        }

        // Priority 3: Check HoverLibrary again for the resolved token
        // (might be different from Priority 0 if we forwarded from namespace qualifier)
        hoverable = Sense.HoverLibrary.Get(token.Range.Start);
        if (hoverable is not null)
        {
            Log.Debug("[HOVER] Priority 3: Returning hoverable for resolved token at position {Pos}", token.Range.Start);
            return hoverable.GetHover();
        }

        // Priority 4: Fallback - try to synthesize basic hover for script functions without SenseDefinition
        // This handles edge cases where analysis might have missed something
        if (token.Type == TokenType.Identifier && !IsBuiltinFunction(token.Lexeme))
        {
            return TryBuildFallbackFunctionHover(token);
        }

        return null;
    }

    /// <summary>
    /// Checks if token is a namespace qualifier (e.g., "utility" in "utility::function")
    /// and returns the actual function token after the ::
    /// </summary>
    private bool TryGetQualifiedFunctionToken(Token token, out Token? functionToken)
    {
        functionToken = null;

        Token? next = token.Next;
        while (next is not null && next.IsWhitespacey())
        {
            next = next.Next;
        }

        if (next is null || next.Type != TokenType.ScopeResolution)
        {
            return false;
        }

        Token? afterScope = next.Next;
        while (afterScope is not null && afterScope.IsWhitespacey())
        {
            afterScope = afterScope.Next;
        }

        if (afterScope is not null && afterScope.Type == TokenType.Identifier)
        {
            functionToken = afterScope;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds hover showing full function documentation when inside a call
    /// </summary>
    private Hover? BuildCallSignatureHover(Token functionToken, int activeParam)
    {
        var (qualifier, funcName) = ParseNamespaceQualifiedIdentifierByIndex(Sense.Tokens.IndexOf(functionToken));

        Log.Debug("[HOVER] BuildCallSignatureHover: funcName='{FuncName}', qualifier='{Qualifier}', senseDef={SenseDefType}",
            funcName, qualifier ?? "(none)", Sense.GetSenseDefinition(functionToken)?.GetType().Name ?? "null");

        // Try to get the function definition to show its full documentation
        ScrFunction? function = null;

        // First, check if the function token has a SenseDefinition with the function
        if (Sense.GetSenseDefinition(functionToken) is ScrFunctionSymbol funcSymbol)
        {
            Log.Debug("[HOVER] Found function via ScrFunctionSymbol");
            function = funcSymbol.Source;
        }
        else if (Sense.GetSenseDefinition(functionToken) is ScrMethodSymbol methodSymbol)
        {
            Log.Debug("[HOVER] Found function via ScrMethodSymbol");
            function = methodSymbol.Source;
        }
        else if (Sense.GetSenseDefinition(functionToken) is ScrFunctionReferenceSymbol refSymbol)
        {
            Log.Debug("[HOVER] Found ScrFunctionReferenceSymbol, looking up function in definitions table");
            // Function reference doesn't contain the full function, need to look it up
            string ns = qualifier ?? GetEffectiveNamespace();

            // Try to get from local definitions first
            var localFunc = DefinitionsTable?.LocalScopedFunctions
                .FirstOrDefault(f => string.Equals(f.Function.Name, funcName, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(f.Function.Namespace, ns, StringComparison.OrdinalIgnoreCase));

            if (localFunc != default)
            {
                Log.Debug("[HOVER] Found function in LocalScopedFunctions: ns={Ns}, name={Name}", ns, funcName);
                function = localFunc.Value.Function;
            }
            else
            {
                // Try internal symbols (from other scripts)
                string qualifiedName = string.IsNullOrEmpty(qualifier) ? funcName : $"{qualifier}::{funcName}";
                if (DefinitionsTable?.InternalSymbols.TryGetValue(qualifiedName, out var symbol) == true && symbol is ScrFunction internalFunc)
                {
                    Log.Debug("[HOVER] Found function in InternalSymbols: qualifiedName={QualifiedName}", qualifiedName);
                    function = internalFunc;
                }
            }
        }

        // If not in sense, check if it's a built-in API function
        if (function is null)
        {
            Log.Debug("[HOVER] Trying API lookup for function '{FuncName}'", funcName);
            var api = TryGetApi();
            if (api is not null)
            {
                function = api.GetApiFunction(funcName);
                if (function is not null)
                {
                    Log.Debug("[HOVER] Found function in API");
                }
            }
        }

        // If we found the function, use its full Documentation
        if (function is not null)
        {
            Log.Information("[HOVER] Using function.Documentation for '{FuncName}'", funcName);
            return new Hover
            {
                Range = functionToken.Range,
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = function.Documentation
                })
            };
        }

        Log.Debug("[HOVER] Function not found, falling back to BuildSignatureMarkdown");

        // Fallback: use simplified signature if function not found
        string? markdown = BuildSignatureMarkdown(funcName, qualifier, activeParam);
        if (markdown is null)
            return null;

        return new Hover
        {
            Range = functionToken.Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = markdown
            })
        };
    }

    /// <summary>
    /// Last resort: build basic hover for script functions that don't have proper SenseDefinition
    /// </summary>
    private Hover? TryBuildFallbackFunctionHover(Token token)
    {
        int tokenIdx = Sense.Tokens.IndexOf(token);
        var (qualifier, name) = tokenIdx >= 0
            ? ParseNamespaceQualifiedIdentifierByIndex(tokenIdx)
            : (null, token.Lexeme);

        // Exclude builtin API functions
        if (IsBuiltinFunction(name))
        {
            return null; // let existing hover (if any) handle API
        }

        // Find function/method in current script tables
        string ns = qualifier ?? GetEffectiveNamespace();
        string? doc = DefinitionsTable?.GetFunctionDoc(ns, name);
        string[]? parameters = DefinitionsTable?.GetFunctionParameters(ns, name);

        // If we don't even know it exists, give up
        if (doc is null && parameters is null)
        {
            var anyLoc = DefinitionsTable?.GetFunctionLocationAnyNamespace(name);
            if (anyLoc is null)
            {
                return null;
            }
            // Known to exist but no metadata - show minimal info
            return new Hover
            {
                Range = token.Range,
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = $"{s_gscCodeBlockStart}function {name}(){s_codeBlockEnd}"
                })
            };
        }

        // Build hover from available metadata
        string[] cleanParams = parameters?.Select(StripDefault).ToArray() ?? Array.Empty<string>();
        string signature = cleanParams.Length == 0
            ? $"function {name}()"
            : $"function {name}({string.Join(", ", cleanParams)})";

        string content = string.IsNullOrWhiteSpace(doc)
            ? $"{s_gscCodeBlockStart}{signature}{s_codeBlockEnd}"
            : $"{s_gscCodeBlockStart}{signature}{s_codeBlockEnd}{s_markdownSeparator}{doc}";

        return new Hover
        {
            Range = token.Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = content
            })
        };
    }

    private string? BuildSignatureMarkdown(string name, string? qualifier, int activeParam)
    {
        // Built-in API: try first
        var api = TryGetApi();
        if (api is not null)
        {
            try
            {
                var apiFn = api.GetApiFunction(name);
                if (apiFn is not null)
                {
                    var overload = apiFn.Overloads.FirstOrDefault();
                    var paramSeq = overload != null ? overload.Parameters : new List<ScrFunctionArg>();
                    string[] names = paramSeq.Select(p => StripDefault(p.Name)).ToArray();
                    string sig = FormatSignature(name, names, activeParam, qualifier);
                    string desc = apiFn.Description ?? string.Empty;
                    return string.IsNullOrEmpty(desc) ? sig : $"{sig}{s_markdownSeparator}{desc}";
                }
            }
            catch { }
        }

        // Script-defined (local or imported)
        string ns = qualifier ?? GetEffectiveNamespace();
        string[]? parms = DefinitionsTable?.GetFunctionParameters(ns, name);
        string? doc = DefinitionsTable?.GetFunctionDoc(ns, name);
        if (parms is not null)
        {
            string sig = FormatSignature(name, parms.Select(StripDefault).ToArray(), activeParam, qualifier);
            // Doc comment is already formatted by SanitizeDocForMarkdown, don't sanitize again
            string formattedDoc = doc ?? string.Empty;
            return string.IsNullOrEmpty(formattedDoc) ? sig : $"{sig}{s_markdownSeparator}{formattedDoc}";
        }

        // Fallback: show empty params signature if symbol exists somewhere
        var any = DefinitionsTable?.GetFunctionLocationAnyNamespace(name);
        if (any is not null)
        {
            return FormatSignature(name, Array.Empty<string>(), activeParam, qualifier);
        }
        return null;
    }

    private static string FormatSignature(string name, IReadOnlyList<string> parameters, int activeParam, string? qualifier)
    {
        string nsPrefix = string.IsNullOrEmpty(qualifier) ? string.Empty : qualifier + "::";
        if (parameters.Count == 0)
        {
            return $"{s_gscCodeBlockStart}function {nsPrefix}{name}(){s_codeBlockEnd}";
        }
        var sb = new StringBuilder();
        sb.Append(s_gscCodeBlockStart).Append("function ").Append(nsPrefix).Append(name).Append('(');
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            string p = StripDefault(parameters[i]);
            if (i == activeParam)
            {
                sb.Append('<').Append(p).Append('>');
            }
            else
            {
                sb.Append(p);
            }
        }
        sb.Append(')').Append(s_codeBlockEnd);
        return sb.ToString();
    }
}
