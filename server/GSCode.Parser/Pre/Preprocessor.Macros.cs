using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace GSCode.Parser.Pre;

internal ref partial struct Preprocessor
{
    private void Macro(MacroDefinition macroDefinition)
    {
        if(macroDefinition.Parameters is null)
        {
            MacroWithoutArgs(macroDefinition);
            return;
        }
        MacroWithArgs(macroDefinition);
    }

    private void MacroWithoutArgs(MacroDefinition macroDefinition)
    {
        LinkedToken macroToken = Consume();

        // Clone the expansion, adding them with the macro token's range
        TokenList expansion = macroDefinition.ExpansionTokens.CloneList(macroToken.TokenRange, markAsPreprocessor: true);

        // Connect them to the surrounding tokens
        expansion.ConnectToTokens(macroToken.Previous!, macroToken.Next!);

        // Make sure we're at the beginning, as macros can contain macros.
        CurrentNode = macroToken.Previous!;

        // Finally, add the macro reference to IntelliSense
        Sense.AddSenseToken(macroToken.Token, new ScriptMacro(macroToken.Token, macroDefinition, expansion));
    }

    private void MacroWithArgs(MacroDefinition macroDefinition)
    {
        // Get the macro token
        LinkedToken macroToken = Consume();

        // The macro should have an arguments list but doesn't, so it'll be ignored.
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddErrorAtLinkedToken(GSCErrorCodes.MissingMacroParameterList, macroToken, macroToken.Lexeme);
            return;
        }

        // Get the arguments
        LinkedList<TokenList?> arguments = MacroArgs(macroToken, macroDefinition.Parameters!);

        // Check for )
        if(!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedPreprocessorToken, ")", CurrentToken.Lexeme);
        }
        LinkedToken endAnchorToken = CurrentNode;

        // Start with a cloned expansion, then replace references to parameters with the argument expansions.
        TokenList expansion = macroDefinition.ExpansionTokens.CloneList(macroToken.TokenRange, markAsPreprocessor: true);

        // Before doing anything to it, connect it to the macro token
        expansion.ConnectToTokens(macroToken.Previous!, endAnchorToken);

        // Use direct LinkedListNode<T> traversal rather than LINQ Zip here.
        // macroDefinition.Parameters is a shared LinkedList<Token> owned by a MacroDefinition
        // that lives in the MacroDefinitionCache. Multiple parser threads can expand the same
        // macro concurrently (e.g. two files that both #insert the same header). A raw
        // LinkedListNode<T> pointer is safe to hold across iterations because cached definitions
        // are never mutated after insertion. An IEnumerator<T>, by contrast, tracks the list's
        // internal version counter and will throw InvalidOperationException — or silently diverge
        // on older runtimes — if any concurrent path touches the same list.
        Dictionary<string, TokenList?> argumentMappings = new();

        LinkedListNode<Token>? parameter = macroDefinition.Parameters!.First;
        LinkedListNode<TokenList?>? argument = arguments.First;

        while(parameter is not null && argument is not null)
        {
            argumentMappings[parameter.Value.Lexeme] = argument.Value;

            parameter = parameter.Next;
            argument = argument.Next;
        }

        // Replace parameter references with their argument expansions.
        LinkedToken? current = expansion.Start;
        while(current is not null)
        {
            // Curren token is a ref
            if((current.Type == TokenType.Identifier || current.IsKeyword()) && argumentMappings.TryGetValue(current.Lexeme, out TokenList? parameterExpansionResult))
            {
                // It's left blank, so just remove the identifier
                if(parameterExpansionResult is not TokenList parameterExpansion)
                {
                    LinkedToken next = current.Next!;
                    ConnectTokens(current.Previous!, next);

                    // Update expansion.Start if needed
                    if (current == expansion.Start)
                    {
                        expansion.Start = next;
                    }
                    // Update expansion.End if needed
                    if (current == expansion.End)
                    {
                        expansion.End = current.Previous;
                        break;
                    }

                    current = next;
                    continue;
                }

                // Otherwise, clone the expansion then connect it to where the identifier formerly was
                TokenList clonedExpansion = parameterExpansion.CloneList();

                clonedExpansion.ConnectToTokens(current.Previous!, current.Next!);

                // Going to current.Next is fine as it's still one-way connected to beyond the end of the expansion
                if (current == expansion.Start)
                {
                    expansion.Start = clonedExpansion.Start;
                }
                if (current == expansion.End)
                {
                    expansion.End = clonedExpansion.End;
                    break;
                }
            }

            if (current == expansion.End)
            {
                break;
            }
            current = current.Next;
        }

        // Then finally, connect the macro token to the expansion
        expansion.ConnectToTokens(macroToken.Previous!, endAnchorToken);

        // Make sure we're at the beginning, as macros can contain macros.
        CurrentNode = macroToken.Previous!;

        // Job done (who knew with args would be so much more complex!)
        // Finally, add the macro reference to IntelliSense
        Sense.AddSenseToken(macroToken.Token, new ScriptMacro(macroToken.Token, macroDefinition, expansion));
    }

    /// <summary>
    /// Parses one or more macro expansion arguments.
    /// </summary>
    /// <param name="macroToken"></param>
    /// <param name="parameters"></param>
    /// <returns></returns>
    private LinkedList<TokenList?> MacroArgs(LinkedToken macroToken, IEnumerable<Token> parameters)
    {
        int expectedParameterCount = parameters.Count();

        // Iterative to avoid stack overflow on pathological macro calls with many arguments.
        LinkedList<TokenList?> result = [];

        // Collect the first argument (always present once we're inside the paren).
        result.AddLast(MacroArgExpansion());
        int index = 1;
        bool alreadyErroredAboutArgumentCount = false;

        while (ConsumeIfType(TokenType.Comma, out LinkedToken? commaToken))
        {
            // Too many arguments — report once, keep parsing.
            if (index + 1 > expectedParameterCount && !alreadyErroredAboutArgumentCount)
            {
                alreadyErroredAboutArgumentCount = true;
                AddErrorAtLinkedToken(GSCErrorCodes.TooManyMacroArguments, commaToken!, macroToken.Lexeme, expectedParameterCount);
            }

            result.AddLast(MacroArgExpansion());
            index++;
        }

        // Too few arguments.
        if (expectedParameterCount != index)
        {
            AddError(GSCErrorCodes.TooFewMacroArguments, macroToken.Lexeme, expectedParameterCount);
        }

        return result;
    }

    /// <summary>
    /// Parses a macro argument's expansion.
    /// </summary>
    /// <returns></returns>
    private TokenList? MacroArgExpansion()
    {
        ExpansionState state = new();

        LinkedToken start = CurrentNode;
        LinkedToken? current = null;

        while(!state.ShouldEndExpansion(CurrentTokenType))
        {
            // Maintain the indexes looking ahead at the token we're about to consume
            state.TrackToken(CurrentTokenType);

            // Go to the next token, consuming our current one
            current = Consume();
        }

        // Nothing to do, it was an empty expansion
        if (current is null)
        {
            return null;
        }

        return new TokenList(start, current);
    }

    private record struct ExpansionState(int ParenIndex = 0, int BraceIndex = 0, int BracketIndex = 0)
    {
        private bool InPunctuation => ParenIndex > 0 || BraceIndex > 0 || BracketIndex > 0;

        public void TrackToken(TokenType currentTokenType)
        {
            switch (currentTokenType)
            {
                case TokenType.OpenParen:
                    ParenIndex++;
                    break;
                case TokenType.CloseParen when ParenIndex > 0:
                    ParenIndex--;
                    break;
                case TokenType.OpenBracket:
                    BracketIndex++;
                    break;
                case TokenType.CloseBracket when BracketIndex > 0:
                    BracketIndex--;
                    break;
                case TokenType.OpenBrace:
                    BraceIndex++;
                    break;
                case TokenType.CloseBrace when BraceIndex > 0:
                    BraceIndex--;
                    break;
            }
        }

        public bool ShouldEndExpansion(TokenType currentTokenType)
        {
            // EOF should always end expansion, even if punctuation is unclosed
            if (currentTokenType == TokenType.Eof)
            {
                return true;
            }

            if (InPunctuation)
            {
                return false;
            }

            return currentTokenType == TokenType.Comma || currentTokenType == TokenType.CloseParen;
        }
    }

    private readonly bool TryGetMacroDefinition(LinkedToken token, [NotNullWhen(true)] out MacroDefinition? definition)
    {
        // Only expand user-defined macros in the third pass.
        // Lookup built-in macros first, because they exist in user-defined space only as placeholders.
        return TryGetSystemDefinedMacroDefinition(token, out definition) || Defines.TryGetValue(token.Lexeme, out definition);
    }

    private readonly bool TryGetSystemDefinedMacroDefinition(LinkedToken token, [NotNullWhen(true)] out MacroDefinition? definition)
    {
        // Built-in macros are expanded first and third passes.
        switch (token.Lexeme)
        {
            case "__LINE__":
                // account for zero-index
                definition = LineMacro(token.Range.Start.Line + 1);
                return true;
            case "XFILE_VERSION":
                definition = XFileVersionMacro;
                return true;
            case "__FILE__":
                definition = FileMacro;
                return true;
            case "__FUNCTION__":
                definition = FunctionMacro(token);
                return true;
            case "FASTFILE":
                definition = FastFileMacro;
                return true;
        }
        definition = default;
        return false;
    }

    private static MacroDefinition LineMacro(int line)
    {
        return MacroDefinition.BuiltInMacroDefinition("__LINE__", new LexemeToken(TokenType.Integer, TokenRange.Empty, line.ToString()));
    }

    private static MacroDefinition XFileVersionMacro { get; } = MacroDefinition.BuiltInMacroDefinition("XFILE_VERSION", new LexemeToken(TokenType.Integer, TokenRange.Empty, "593"));

    private static MacroDefinition FunctionMacro(LinkedToken token)
    {
        // Walk backwards through the token stream to find the enclosing function name.
        // Pattern: `function <name>` where <name> is an identifier.
        LinkedToken? current = token.Previous;
        while (current is not null && current.Type != TokenType.Sof)
        {
            if (current.Type == TokenType.Function)
            {
                // The function name is the next non-whitespace token after `function`.
                LinkedToken? nameToken = current.Next;
                while (nameToken is not null && nameToken.Type != TokenType.Eof && nameToken.IsWhitespacey())
                    nameToken = nameToken.Next;
                if (nameToken is not null && nameToken.Type == TokenType.Identifier)
                {
                    return MacroDefinition.BuiltInMacroDefinition("__FUNCTION__",
                        new LexemeToken(TokenType.String, TokenRange.Empty, $"\"{nameToken.Lexeme}\""));
                }
                break;
            }
            current = current.Previous;
        }

        // Not inside a function — produce empty string.
        return MacroDefinition.BuiltInMacroDefinition("__FUNCTION__",
            new LexemeToken(TokenType.String, TokenRange.Empty, "\"\""));
    }

    private static MacroDefinition FileMacro { get; } = MacroDefinition.BuiltInMacroDefinition("__FILE__", new LexemeToken(TokenType.Identifier, TokenRange.Empty, "these_wont_work_how_you_hope_them_to_sad_face"));
    private static MacroDefinition FastFileMacro { get; } = MacroDefinition.BuiltInMacroDefinition("FASTFILE", new LexemeToken(TokenType.Identifier, TokenRange.Empty, "these_wont_work_how_you_hope_them_to_sad_face"));
}
