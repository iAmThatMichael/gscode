using GSCode.Data;
using GSCode.Parser.Lexical;
using System.Text.RegularExpressions;

namespace GSCode.Parser.Pre;

internal ref partial struct Preprocessor
{
    [GeneratedRegex(@"(^[\\/])|(^[a-zA-Z]:)|(\.\.)")]
    private static partial Regex InvalidPathRegex();

    /// <summary>
    /// Transforms an insert directive into the file's contents.
    /// </summary>
    private void Insert()
    {
        // Pass over INSERT
        LinkedToken insertToken = Consume();

        // Continously consume tokens until we get to a semicolon or line break.
        TokenList path = Path(out LinkedToken? terminatorToken);

        if(path.IsEmpty)
        {
            // Pass over semicolon so we can remove it too - if it's the others, we don't want to remove those.
            AdvanceIfType(TokenType.Semicolon);

            ConnectTokens(insertToken.Previous!, CurrentNode);
            return;
        }

        // Check the path is relative and doesn't exit the project directory
        string filePath = path.ToRawString();
        if(InvalidPathRegex().IsMatch(filePath))
        {
            AddErrorAtRange(GSCErrorCodes.InvalidInsertPath, path.Range!, filePath);

            if(terminatorToken!.Type == TokenType.Semicolon)
            {
                ConnectTokens(insertToken.Previous!, terminatorToken.Next!);
                return;
            }
            ConnectTokens(insertToken.Previous!, terminatorToken!.Previous!);
            return;
        }

        // Record hover on the #insert path text for navigation
        Sense.HoverLibrary?.Add(new InsertDirectiveHover(filePath, path.Range!));
        // Track insert region and its resolved path to later attribute definitions
        string? resolvedInsertPath = Sense.ResolveInsertPath(filePath, path.Range!);
        Sense.AddInsertRegion(path.Range!, filePath, resolvedInsertPath);

        // If the path couldn't be resolved, the error was already added by ResolveInsertPath.
        // Remove the insert directive tokens so they don't trip the AST parser.
        if (resolvedInsertPath is null)
        {
            RemoveInsertDirective(insertToken, terminatorToken);
            return;
        }

        // Get the file contents — preserve original .gsh token ranges so macro definition
        // navigation can jump to the exact #define line in the inserted file.
        TokenList? insertTokensResult;
        try
        {
            insertTokensResult = Sense.GetFileTokens(filePath);
        }
        catch(Exception ex)
        {
            Sense.AddIdeDiagnostic(path.Range!, GSCErrorCodes.FailedToReadInsertFile, filePath, ex.GetType().Name);
            RemoveInsertDirective(insertToken, terminatorToken);
            return;
        }

        // If we got null back then the file doesn't exist (shouldn't happen since we checked resolvedInsertPath)
        if (insertTokensResult is not TokenList insertTokens)
        {
            RemoveInsertDirective(insertToken, terminatorToken);
            return;
        }

        // Strip comments from inserted tokens - they shouldn't carry over
        StripCommentsFromTokenList(insertTokens);

        // Check if the inserted file is empty (only contains SOF and EOF, or only comments)
        // If empty, just remove the insert directive and continue
        if (insertTokens.Start!.Next == insertTokens.End)
        {
            // Empty file - skip past the insert directive
            if (terminatorToken!.Type == TokenType.Semicolon)
            {
                ConnectTokens(insertToken.Previous!, terminatorToken.Next!);
                CurrentNode = terminatorToken.Next!;
                return;
            }
            ConnectTokens(insertToken.Previous!, terminatorToken);
            CurrentNode = terminatorToken;
            return;
        }

        // Mark all inserted tokens as from preprocessor to prevent duplicate semantic highlights,
        // and stamp each one with the resolved GSH path so the macro attributor can read it
        // directly instead of guessing via host-file line-number comparisons.
        MarkTokenListAsPreprocessor(insertTokens, resolvedInsertPath);

        // Otherwise, it's a unique instance so connect its boundaries (exc. the SOF and EOF) to the insert directive
        ConnectTokens(insertToken.Previous!, insertTokens.Start!.Next!);
        CurrentNode = insertTokens.Start.Next!;

        // Connect past semicolon - otherwise directly to the terminator
        if (terminatorToken!.Type == TokenType.Semicolon)
        {
            ConnectTokens(insertTokens.End!.Previous!, terminatorToken.Next!);
            return;
        }
        ConnectTokens(insertTokens.End!.Previous!, terminatorToken);
    }

    /// <summary>
    /// Removes the insert directive tokens from the token stream so they don't trip the AST parser.
    /// </summary>
    private void RemoveInsertDirective(LinkedToken insertToken, LinkedToken? terminatorToken)
    {
        if (terminatorToken?.Type == TokenType.Semicolon)
        {
            ConnectTokens(insertToken.Previous!, terminatorToken.Next!);
            CurrentNode = terminatorToken.Next!;
        }
        else if (terminatorToken is not null)
        {
            ConnectTokens(insertToken.Previous!, terminatorToken);
            CurrentNode = terminatorToken;
        }
        else
        {
            ConnectTokens(insertToken.Previous!, CurrentNode);
        }
    }

    /// <summary>
    /// Marks all tokens in a token list as originating from preprocessor expansion,
    /// and stamps each with the resolved insert source path so macro attribution can
    /// read it directly from the token instead of inferring it from line numbers.
    /// </summary>
    private static void MarkTokenListAsPreprocessor(TokenList tokenList, string? insertSourcePath)
    {
        if (tokenList.Start is null || tokenList.End is null)
            return;

        LinkedToken current = tokenList.Start;
        while (current is not null)
        {
            current.Token.IsFromPreprocessor = true;
            current.Token.InsertSourcePath = insertSourcePath;
            if (current == tokenList.End)
                break;
            current = current.Next!;
        }
    }

    /// <summary>
    /// Removes all comment tokens from a token list by unlinking them.
    /// Comments from inserted files shouldn't carry over to the main script.
    /// </summary>
    private void StripCommentsFromTokenList(TokenList tokenList)
    {
        if (tokenList.Start is null || tokenList.End is null)
            return;

        LinkedToken current = tokenList.Start.Next!; // Skip SOF
        while (current != tokenList.End)
        {
            LinkedToken next = current.Next!;

            if (current.IsComment())
            {
                // Unlink the comment token
                ConnectTokens(current.Previous!, next);
            }

            current = next;
        }
    }

    private TokenList Path(out LinkedToken? terminatorIfMatched)
    {
        // Empty path
        if(CurrentTokenType == TokenType.Semicolon || CurrentTokenType == TokenType.LineBreak || CurrentTokenType == TokenType.Eof)
        {
            terminatorIfMatched = null;
            AddError(GSCErrorCodes.ExpectedInsertPath);
            return TokenList.Empty;
        }

        LinkedToken start = Consume();
        LinkedToken current = start;

        while(CurrentTokenType != TokenType.Semicolon && CurrentTokenType != TokenType.LineBreak && CurrentTokenType != TokenType.Eof)
        {
            current = Consume();
        }

        // Consume one more time so we can get the terminator and go back one from it for the full, whitespace-included path.
        terminatorIfMatched = Consume();

        if(terminatorIfMatched.Type == TokenType.LineBreak || terminatorIfMatched.Type == TokenType.Eof)
        {
            AddErrorAtLinkedToken(GSCErrorCodes.ExpectedPreprocessorToken, terminatorIfMatched, ';', terminatorIfMatched.Lexeme);
        }

        return new TokenList(start, terminatorIfMatched.Previous);
    }
}
