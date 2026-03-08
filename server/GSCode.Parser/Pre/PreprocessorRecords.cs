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
/// Records script defines for semantics & usage
/// </summary>
/// <param name="Source">The source name token</param>
/// <param name="DefineSnippet">Pre-computed string representation of the define</param>
/// <param name="ExpansionTokens">When used, the key expands to these tokens (only for actual expansion)</param>
/// <param name="Parameters">List of parameters</param>
/// <param name="Documentation">Documentation for the define if it ends in a comment</param>
internal record MacroDefinition(Token Source, string DefineSnippet, TokenList ExpansionTokens,
   LinkedList<Token>? Parameters, string? Documentation = null) : ISenseDefinition
{
    public bool IsFromPreprocessor { get; } = Source.IsFromPreprocessor;
    public Range Range { get; } = Source.Range;

    public string SemanticTokenType { get; } = "macro";
    public string[] SemanticTokenModifiers { get; } = [];

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
        Token sourceToken = new(TokenType.Identifier, RangeHelper.Empty, source)
        {
            IsFromPreprocessor = true
        };

        // Pre-compute the define snippet string
        StringBuilder sb = new();
        sb.Append("#define ");
        sb.Append(source);
        if (expansion.Length > 0)
        {
            sb.Append(' ');
            for (int i = 0; i < expansion.Length; i++)
            {
                sb.Append(expansion[i].Lexeme);
            }
        }
        string defineSnippet = sb.ToString();

        TokenList expansionSource = TokenList.From(expansion);

        MacroDefinition uncached = new(sourceToken, defineSnippet, expansionSource, null);

        // Use cache for built-in macros to avoid duplication across files
        return MacroDefinitionCache.Instance.GetOrAdd(null, source, uncached);
    }
}

/// <summary>
/// Records usages of a macro for semantics & hovers
/// </summary>
/// <param name="Source">The macro token source</param>
/// <param name="DefineSource">The define this macro is from</param>
/// <param name="ExpansionSnippet">Pre-computed string representation of the expansion</param>
internal record ScriptMacro(Token Source, MacroDefinition DefineSource, string ExpansionSnippet) : ISenseDefinition
{
    public bool IsFromPreprocessor { get; } = Source.IsFromPreprocessor;
    public Range Range { get; } = Source.Range;

    public string SemanticTokenType { get; } = OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokenType.Macro;

    public string[] SemanticTokenModifiers { get; } = Array.Empty<string>();

    public Hover GetHover()
    {
        string defineSnippet = DefineSource.DefineSnippet;

        Hover hover = new()
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```\n{1}\n\n---\n{2}\n```gsc\n{3}\n```",
                    defineSnippet, GetFormattedDocumentation(), "Expands to:", ExpansionSnippet)
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