using GSCode.Data;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Pre;

/// <summary>
/// Records script defines for semantics & usage.
/// TokenList fields are mutable so they can be released after preprocessing to allow GC
/// of LinkedToken chains. Snippet strings are cached eagerly at construction time.
/// </summary>
internal class MacroDefinition : ISenseDefinition
{
    public Token Source { get; }
    public string? Documentation { get; }
    public bool IsBuiltIn { get; }
    public Range Range { get; }

    /// <summary>
    /// Cached snippet of the full #define line. Computed eagerly; survives token release.
    /// </summary>
    public string DefineSnippet { get; }

    /// <summary>
    /// The expansion tokens for this macro — used during preprocessing for CloneList.
    /// Released after preprocessing completes.
    /// </summary>
    internal TokenList ExpansionTokens { get; private set; }

    /// <summary>
    /// Parameter list for parameterised macros.
    /// </summary>
    internal LinkedList<Token>? Parameters { get; private set; }

    public string SemanticTokenType { get; } = "macro";
    public string[] SemanticTokenModifiers { get; } = [];

    public MacroDefinition(Token source, TokenList defineTokens, TokenList expansionTokens,
        LinkedList<Token>? parameters, string? documentation = null, bool isBuiltIn = false)
    {
        Source = source;
        Documentation = documentation;
        IsBuiltIn = isBuiltIn;
        Range = source.Range;
        DefineSnippet = defineTokens.ToSnippetString();
        ExpansionTokens = expansionTokens;
        Parameters = parameters;
    }

    /// <summary>
    /// Release LinkedToken chains to allow GC after preprocessing.
    /// The cached snippet strings remain available.
    /// </summary>
    public void ReleaseTokenLists()
    {
        ExpansionTokens = TokenList.Empty;
        Parameters = null;
    }

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```\n{1}",
                    DefineSnippet, GetFormattedDocumentation())
            })
        };
    }

    private string GetFormattedDocumentation()
    {
        if (!string.IsNullOrEmpty(Documentation))
        {
            return string.Format("---\n{0}", Documentation);
        }
        return string.Empty;
    }

    public static MacroDefinition BuiltInMacroDefinition(string source, params Token[] expansion)
    {
        LexemeToken sourceToken = new(TokenType.Identifier, TokenRange.Empty, source);

        // Create a combined array of all tokens for the define source
        Token[] defineTokens = [
            new Token(TokenType.Define, TokenRange.Empty),
            new LexemeToken(TokenType.Whitespace, TokenRange.Empty, " "),
            sourceToken,
            new LexemeToken(TokenType.Whitespace, TokenRange.Empty, " "),
            .. expansion,
        ];

        TokenList defineSource = TokenList.From(defineTokens);
        TokenList expansionSource = TokenList.From(expansion);

        MacroDefinition uncached = new(sourceToken, defineSource, expansionSource, null, isBuiltIn: true);

        // Use cache for built-in macros to avoid duplication across files
        return MacroDefinitionCache.Instance.GetOrAdd(null, source, uncached);
    }
}

/// <summary>
/// Records usages of a macro for semantics & hovers.
/// TokenList field is mutable so it can be released after preprocessing.
/// </summary>
internal class ScriptMacro : ISenseDefinition
{
    public Token Source { get; }
    public MacroDefinition DefineSource { get; }
    public Range Range { get; }

    /// <summary>
    /// Cached snippet of the expansion. Computed eagerly; survives token release.
    /// </summary>
    public string ExpansionSnippet { get; }

    public string SemanticTokenType { get; } = OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokenType.Macro;
    public string[] SemanticTokenModifiers { get; } = Array.Empty<string>();

    public ScriptMacro(Token source, MacroDefinition defineSource, TokenList expansionTokens)
    {
        Source = source;
        DefineSource = defineSource;
        Range = source.Range;
        ExpansionSnippet = expansionTokens.ToSnippetString();
        // Don't store expansionTokens — snippet is cached, no further use needed
    }

    public Hover GetHover()
    {
        Hover hover = new()
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```\n{1}\n\n---\n{2}\n```gsc\n{3}\n```",
                    DefineSource.DefineSnippet, GetFormattedDocumentation(), "Expands to:", ExpansionSnippet)
            }),
            Range = Source.Range,
        };

        return hover;
    }

    private string GetFormattedDocumentation()
    {
        if (!string.IsNullOrEmpty(DefineSource.Documentation))
        {
            return string.Format("---\n{0}", DefineSource.Documentation);
        }
        return string.Empty;
    }
}

/// <summary>
/// Hoverable entry for an #insert directive path to enable navigation and quick info on that line.
/// </summary>
/// <param name="RawPath">The raw path text inside the directive</param>
/// <param name="Range">The range that covers the path text</param>
internal sealed record InsertDirectiveHover(string RawPath, Range Range) : IHoverable
{
    public Hover GetHover()
    {
        return new Hover
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = $"#insert {RawPath}"
            })
        };
    }
}
