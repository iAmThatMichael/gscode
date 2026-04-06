using GSCode.Data;
using GSCode.Parser.Lexical;
using System.Collections.Generic;

namespace GSCode.Parser.Pre;

internal ref partial struct Preprocessor
{
    /// <summary>
    /// Transforms a macro definition into a script define.
    /// </summary>
    private void Define()
    {
        // Pass over DEFINE
        LinkedToken defineToken = Consume();

        // Get the macro name
        LinkedToken nameToken = CurrentNode;

        // Macros can be either keywords or identifiers
        if (CurrentTokenType != TokenType.Identifier && !CurrentToken.IsKeyword())
        {
            AddError(GSCErrorCodes.ExpectedMacroIdentifier, CurrentToken.Lexeme);
            return;
        }

        CurrentNode = CurrentNode.Next!;

        string macroName = nameToken.Lexeme;

        // Get its parameter list, if it has one.
        LinkedList<Token>? parameters = ParamList();

        // In order to exclude backslashes/linebreaks, we'll have to create a copy of the token list for the expansion
        LinkedToken? firstExpansionNode = null;
        LinkedToken? lastExpansionNode = null;
        LinkedToken? previousNode = null;
        LinkedToken current = CurrentNode;

        while (current.Type != TokenType.LineBreak && current.Type != TokenType.Eof)
        {
            // Handle backslash here, which must immediately precede a line break if encountered
            if (current.Type == TokenType.Backslash)
            {
                if (current.Next!.Type != TokenType.LineBreak)
                {
                    AddError(GSCErrorCodes.InvalidLineContinuation, "\\");
                }
                else
                {
                    // Skip both the backslash and linebreak
                    current = current.Next!.Next!;  // Skip past backslash and linebreak
                    continue;  // Continue to next token without adding these to expansion
                }
            }

            // Clone the current token and link it
            var newNode = new LinkedToken(current.Token with { });

            firstExpansionNode ??= newNode;
            newNode.Previous = previousNode ?? newNode;
            if (previousNode is not null)
            {
                previousNode.Next = newNode;
            }

            previousNode = newNode;
            lastExpansionNode = newNode;
            current = current.Next!;
        }
        CurrentNode = defineToken.Previous!;

        // Consume skips comments, so if there is one at the end of this expansion we can get it directly from working backwards from CurrentToken
        LinkedToken documentationToken = current.Previous!;
        string? documentation = null;

        // TODO: this currently doesn't remove the //, etc.
        if (documentationToken.IsComment())
        {
            documentation = documentationToken.Lexeme;
        }

        // Remove the define directive from the script.
        ConnectTokens(defineToken.Previous!, current.Next!);

        // Create the macro (will be cached to avoid duplication across files)
        MacroDefinition uncachedDefinition = new(
            nameToken.Token,
            new TokenList(defineToken, current),
            new TokenList(firstExpansionNode, lastExpansionNode),
            parameters,
            documentation
            );

        // Determine source file path for caching
        string? sourceFilePath = null;
        string srcDisplay;
        if (nameToken.Token.IsFromPreprocessor)
        {
            // This macro came from an insert, find which insert region it belongs to
            foreach (var region in Sense.InsertRegions)
            {
                // Find the insert region that this macro line falls within
                if (region.Range.Start.Line <= nameToken.Range.Start.Line &&
                    region.ResolvedPath is not null)
                {
                    sourceFilePath = region.ResolvedPath;
                    // Keep updating as we find later regions (use the most recent/closest one)
                }
            }
            srcDisplay = GetRelativeDisplay(sourceFilePath ?? Sense.ScriptUri);
        }
        else
        {
            // Local macro - use the current script path
            sourceFilePath = Sense.ScriptPath;
            srcDisplay = GetRelativeDisplay(Sense.ScriptUri);
        }

        // Use the cache to deduplicate identical macros across files
        MacroDefinition definition = MacroDefinitionCache.Instance.GetOrAdd(sourceFilePath, macroName, uncachedDefinition);

        // GSC doesn't allow redefinitions of existing macros.
        if(TryGetMacroDefinition(nameToken, out _))
        {
            Sense.AddPreDiagnostic(nameToken.Range, GSCErrorCodes.DuplicateMacroDefinition, macroName);
        }
        else
        {
            // Fine to add
            Defines.Add(macroName, definition);

            Sense.AddMacroOutline(macroName, nameToken.Range, srcDisplay);
            Sense.AddMacroDefinition(macroName, definition, srcDisplay);
        }
        Sense.AddSenseToken(nameToken.Token, definition);

        static string GetRelativeDisplay(string fullPath)
        {
            try
            {
                // Show trailing two segments if possible, e.g., shared/shared.gsh
                string dir = System.IO.Path.GetDirectoryName(fullPath) ?? string.Empty;
                string file = System.IO.Path.GetFileName(fullPath);
                string lastDir = string.IsNullOrEmpty(dir) ? string.Empty : System.IO.Path.GetFileName(dir);
                string result = string.IsNullOrEmpty(lastDir) ? file : System.IO.Path.Combine(lastDir, file).Replace('\\', '/');
                // Intern the result since the same display path will be used by many macros from the same file
                return Lexical.StringPool.Intern(result);
            }
            catch { return fullPath; }
        }
    }

    /// <summary>
    /// Consumes an #elif directive (including its condition) in the case that it's misplaced, so it doesn't cause
    /// AST errors.
    /// </summary>
    private void SkipMisplacedDirective()
    {
        LinkedToken startToken = CurrentNode;
        LinkedToken current = CurrentNode;

        // Scan until newline or EOF
        while (current.Next is not null && current.Next.Type != TokenType.LineBreak && current.Next.Type != TokenType.Eof)
        {
            current = current.Next;
        }

        // Remove the directive and condition tokens but preserve the newline/EOF
        LinkedToken endToken = current;
        ConnectTokens(startToken.Previous!, endToken.Next!);

        // Update current token to point after the removed section
        CurrentNode = endToken.Next!;
    }

    /// <summary>
    /// Parses a macro definition's parameter list, if it exists.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token>? ParamList()
    {
        // No parameters
        if(!AdvanceIfType(TokenType.OpenParen))
        {
            return null;
        }

        LinkedList<Token> result = Params();

        // Check for CLOSEPAREN
        if(!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedPreprocessorToken, ")", CurrentToken.Lexeme);
        }

        return result;
    }

    /// <summary>
    /// Parses zero or more macro definition parameters.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token> Params()
    {
        // Used to duplicate check
        HashSet<string> set = [];

        // Zero parameters
        if (!ConsumeIfType(TokenType.Identifier, out LinkedToken? parameterNode))
        {
            return [];
        }

        set.Add(parameterNode.Lexeme);

        LinkedList<Token> rest = ParamsRhs(set);
        rest.AddFirst(parameterNode.Token);

        return rest;
    }

    /// <summary>
    /// Parses the right-hand side of a macro definition's parameters.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token> ParamsRhs(HashSet<string> set)
    {
        // End of parameter list
        if (!ConsumeIfType(TokenType.Comma, out LinkedToken? commaToken))
        {
            return [];
        }

        // Get the next parameter's name
        if (CurrentTokenType != TokenType.Identifier && !CurrentToken.IsKeyword())
        {
            AddError(GSCErrorCodes.ExpectedMacroParameter, CurrentToken.Lexeme);
            return [];
        }
        LinkedToken parameterNode = Consume();

        // Duplicate parameter
        bool isDuplicate = !set.Add(parameterNode.Lexeme);
        if(isDuplicate)
        {
            AddErrorAtLinkedToken(GSCErrorCodes.DuplicateMacroParameter, parameterNode, parameterNode.Lexeme);
        }

        // Recurse
        LinkedList<Token> rest = ParamsRhs(set);
        if(!isDuplicate)
        {
            rest.AddFirst(parameterNode.Token);
        }

        return rest;
    }
}
