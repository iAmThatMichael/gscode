using System.Diagnostics.CodeAnalysis;
using GSCode.Data;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.AST;

/// <summary>
/// An implementation of an LL(1) recursive descent parser for the GSC & CSC languages.
/// </summary>
internal ref partial struct Parser(LinkedToken startNode, ParserIntelliSense sense, string languageId)
{
    public LinkedToken PreviousNode { get; private set; } = startNode;
    public LinkedToken CurrentNode { get; private set; } = startNode;

    public Token CurrentToken => CurrentNode.Token;
    public readonly TokenType CurrentTokenType => CurrentNode.Type;
    public readonly Range CurrentTokenRange => CurrentNode.Range;

    [Flags]
    private enum ParserContextFlags
    {
        None = 0,
        InFunctionBody = 1,
        InSwitchBody = 2,
        InLoopBody = 4,
        InDevBlock = 8,
    }

    private ParserContextFlags ContextFlags { get; set; } = ParserContextFlags.None;

    // TODO: temp hack to add function hoverables in current version.
    // Use shared API instance to avoid redundant allocations
    private readonly ScriptAnalyserData? _scriptAnalyserData = ScriptAnalyserData.GetShared(languageId);

    public ParserIntelliSense Sense { get; } = sense;

    /// <summary>
    /// Used by fault recovery strategies to allow them to attempt parsing in a fault state.
    /// </summary>
    public LinkedToken SnapshotNode { get; private set; } = startNode;

    /// <summary>
    /// Suppresses all error messages issued when active, which aids with error recovery.
    /// </summary>
    private bool Silent { get; set; } = false;

    public ScriptNode Parse()
    {
        // Advance past the first SOF token.
        Advance();

        return Script();
    }

    /// <summary>
    /// Parses and outputs a script node.
    /// </summary>
    /// <remarks>
    /// Script := DependenciesList ScriptDefnList
    /// </remarks>
    /// <returns></returns>
    private ScriptNode Script()
    {
        List<DependencyNode> dependencies = DependenciesList();
        List<AstNode> scriptDefns = ScriptList();

        return new ScriptNode
        {
            Dependencies = dependencies,
            ScriptDefns = scriptDefns
        };
    }

    // Declaration, statement, and expression parsing methods are in
    // Parser.Declarations.cs, Parser.Statements.cs, and Parser.Expressions.cs respectively.

    private bool InDevBlock()
    {
        return (ContextFlags & ParserContextFlags.InDevBlock) != 0;
    }

    private bool InLoopOrSwitch()
    {
        return (ContextFlags & ParserContextFlags.InLoopBody) != 0 || (ContextFlags & ParserContextFlags.InSwitchBody) != 0;
    }

    private bool InLoop()
    {
        return (ContextFlags & ParserContextFlags.InLoopBody) != 0;
    }

    private bool EnterContextIfNewly(ParserContextFlags context)
    {
        // Already in this context further down.
        if ((ContextFlags & context) != 0)
        {
            return false;
        }

        ContextFlags |= context;
        return true;
    }

    private bool ExitContextIfWasNewly(ParserContextFlags context, bool wasNewly)
    {
        if (wasNewly)
        {
            ContextFlags ^= context;
        }
        return wasNewly;
    }


    private void EnterRecovery()
    {
        Silent = true;
        SnapshotNode = CurrentNode;
    }

    private void ExitRecovery()
    {
        Silent = false;
    }

    private bool InRecovery() => Silent;

    private void Advance()
    {
        PreviousNode = CurrentNode;
        do
        {
            CurrentNode = CurrentNode.Next!;
        }
        // Ignore all whitespace and comments.
        while (
            CurrentTokenType == TokenType.Whitespace ||
            CurrentTokenType == TokenType.LineComment ||
            CurrentTokenType == TokenType.MultilineComment ||
            CurrentTokenType == TokenType.DocComment ||
            CurrentTokenType == TokenType.LineBreak);
    }

    private Token Consume()
    {
        Token consumed = CurrentToken;
        Advance();

        return consumed;
    }

    private bool AdvanceIfType(TokenType type)
    {
        if (CurrentTokenType == type)
        {
            Advance();
            return true;
        }

        return false;
    }

    // next: This is not my preferred method of emitting folding ranges, may move to SPA later.
    private void EmitFoldingRangeIfPossible(Token? openToken, Token? closeToken)
    {
        if (!Sense.IsEditorMode || openToken is null || closeToken is null)
        {
            return;
        }

        Sense.FoldingRanges.Add(new FoldingRange()
        {
            StartLine = openToken.Range.End.Line,
            StartCharacter = openToken.Range.End.Character,

            EndLine = closeToken.Range.Start.Line,
            EndCharacter = closeToken.Range.Start.Character
        });
    }

    private bool ConsumeIfType(TokenType type, [NotNullWhen(true)] out Token? consumed)
    {
        Token current = CurrentToken;
        if (AdvanceIfType(type))
        {
            consumed = current;
            return true;
        }

        consumed = default;
        return false;
    }

    private void AddError(GSCErrorCodes errorCode, params object?[] args)
    {
        // We're in a fault recovery state
        if (Silent)
        {
            return;
        }

        Sense.AddAstDiagnostic(CurrentTokenRange, errorCode, args);
    }

    private void AddErrorAtEndOfPrevious(GSCErrorCodes errorCode, params object?[] args)
    {
        // We're in a fault recovery state
        if (Silent)
        {
            return;
        }

        Sense.AddAstDiagnostic(RangeHelper.From(new Position(PreviousNode.Range.End.Line, Math.Max(0, PreviousNode.Range.End.Character - 1)), PreviousNode.Range.End), errorCode, args);
    }
}


/// <summary>
/// Records the definition of a function parameter for semantics & hovers
/// </summary>
/// <param name="Source">The parameter source</param>
// internal record DumbFunctionSymbol(Token Token, ScrFunctionDefinition Source) : ISenseDefinition
// {
//     // I'm pretty sure this is redundant
//     public bool IsFromPreprocessor { get; } = false;
//     public Range Range { get; } = Token.Range;

//     public string SemanticTokenType { get; } = "function";
//     public string[] SemanticTokenModifiers { get; } = [];

//     public Hover GetHover()
//     {
//         return new()
//         {
//             Range = Range,
//             Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
//             {
//                 Kind = MarkupKind.Markdown,
//                 Value = Source.Documentation
//             })
//         };
//     }
// }
