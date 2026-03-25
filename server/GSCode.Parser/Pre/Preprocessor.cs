using GSCode.Data;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace GSCode.Parser.Pre;

internal ref partial struct Preprocessor(LinkedToken startNode, ParserIntelliSense sense)
{
    private LinkedToken CurrentNode { get; set; } = startNode;

    private Token CurrentToken => CurrentNode.Token;
    private readonly TokenType CurrentTokenType => CurrentNode.Type;
    private readonly Range CurrentTokenRange => CurrentNode.Range;

    private ParserIntelliSense Sense { get; } = sense;

    private Dictionary<string, MacroDefinition> Defines { get; } = new();

    public void Process()
    {
        LinkedToken startToken = CurrentNode;

        // pass 1 - expand all system-defined macros, track all defines and apply inserts.
        // pass 2 - evaluate all #if/#elif directives.
        // pass 3 - expand all user-defined macros and system-defined macros as a result of those (if necessary).
        FirstPass();
        CurrentNode = startToken;

        SecondPass();
        CurrentNode = startToken;

        ThirdPass();

        // NOTE: Do NOT call ReleaseTokenLists() here. MacroDefinition instances are shared
        // across concurrent parsers via the global MacroDefinitionCache. Releasing them here
        // would destroy Parameters/ExpansionTokens that other threads are still reading.
        // The cache manages object lifetime through RemoveFileMacros/Clear.
    }

    /// <summary>
    /// Performs preprocessor transformations on the script token sequence.
    /// </summary>
    public void FirstPass()
    {
        while(CurrentTokenType != TokenType.Eof)
        {
            switch(CurrentTokenType)
            {
                case TokenType.Define:
                    Define();
                    break;
                case TokenType.Insert:
                    Insert();
                    break;
                default:
                    // is it a system macro reference?
                    if((CurrentTokenType == TokenType.Identifier || CurrentToken.IsKeyword()) &&
                        TryGetSystemDefinedMacroDefinition(CurrentNode, out MacroDefinition? macro))
                    {
                        Macro(macro);
                        break;
                    }
                    Advance();
                    break;
            }
        }
    }

    public void SecondPass()
    {
        while (CurrentTokenType != TokenType.Eof)
        {
            switch (CurrentTokenType)
            {
                case TokenType.PreIf:
                    IfDirective();
                    break;
                case TokenType.PreElIf:
                    AddError(GSCErrorCodes.MisplacedPreprocessorDirective, CurrentToken.Lexeme);
                    // Consume the directive so it doesn't cause further issues
                    SkipMisplacedDirective();

                    break;
                case TokenType.PreElse:
                case TokenType.PreEndIf:
                    AddError(GSCErrorCodes.MisplacedPreprocessorDirective, CurrentToken.Lexeme);

                    // Delete the directive so it doesn't cause further issues
                    ConnectTokens(CurrentNode.Previous!, CurrentNode.Next!);
                    Advance();
                    break;
                default:
                    Advance();
                    break;
            }
        }
    }

    public void ThirdPass()
    {
        // Now expand user-defined macros and any system-defined macros that weren't expanded in the first pass.
        while(CurrentTokenType != TokenType.Eof)
        {
            if((CurrentTokenType == TokenType.Identifier || CurrentToken.IsKeyword()) &&
                TryGetMacroDefinition(CurrentNode, out MacroDefinition? macro))
            {
                Macro(macro);
                continue;
            }
            Advance();
        }
    }

    private void ConnectTokens(LinkedToken left, LinkedToken right)
    {
        left.Next = right;
        right.Previous = left;
    }


    private void AddError(GSCErrorCodes errorCode, params object?[] args)
    {
        Sense.AddPreDiagnostic(CurrentTokenRange, errorCode, args);
    }

    private void AddErrorAtToken(GSCErrorCodes errorCode, Token token, params object?[] args)
    {
        Sense.AddPreDiagnostic(token.Range, errorCode, args);
    }

    private void AddErrorAtLinkedToken(GSCErrorCodes errorCode, LinkedToken token, params object?[] args)
    {
        Sense.AddPreDiagnostic(token.Range, errorCode, args);
    }

    private void AddErrorAtRange(GSCErrorCodes errorCode, Range range, params object?[] args)
    {
        Sense.AddPreDiagnostic(range, errorCode, args);
    }

    private void Advance()
    {
        do
        {
            CurrentNode = CurrentNode.Next!;
        }
        // Ignore all whitespace and comments, but don't ignore line breaks.
        while (
            CurrentTokenType == TokenType.Whitespace ||
            CurrentTokenType == TokenType.LineComment ||
            CurrentTokenType == TokenType.MultilineComment ||
            CurrentTokenType == TokenType.DocComment);
    }

    private LinkedToken Consume()
    {
        LinkedToken consumed = CurrentNode;
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

    private bool ConsumeIfType(TokenType type, [NotNullWhen(true)] out LinkedToken? consumed)
    {
        LinkedToken current = CurrentNode;
        if (AdvanceIfType(type))
        {
            consumed = current;
            return true;
        }

        consumed = default;
        return false;
    }

}
