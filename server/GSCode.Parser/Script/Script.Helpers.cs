using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using GSCode.Parser.Util;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser;

public partial class Script
{
    // --- Index-based helpers ---

    /// <summary>
    /// Index-based: checks if identifier token is preceded by &amp; (address-of operator).
    /// </summary>
    private bool IsAddressOfIdentifierByIndex(int tokenIndex)
    {
        var tokens = Sense.Tokens;
        int leftMostIdx = tokenIndex;
        // Check for ns::name pattern
        int prevIdx = tokens.PrevNonTriviaIndex(leftMostIdx);
        if (prevIdx >= 0 && tokens.GetAt(prevIdx)!.Type == TokenType.ScopeResolution)
        {
            int nsIdx = tokens.PrevNonTriviaIndex(prevIdx);
            if (nsIdx >= 0 && tokens.GetAt(nsIdx)!.Type == TokenType.Identifier)
            {
                leftMostIdx = nsIdx;
            }
        }
        int beforeIdx = tokens.PrevNonTriviaIndex(leftMostIdx);
        return beforeIdx >= 0 && tokens.GetAt(beforeIdx)!.Type == TokenType.BitAnd;
    }

    /// <summary>
    /// Index-based: parse namespace::name or just name from a token at the given index.
    /// </summary>
    private (string? qualifier, string name) ParseNamespaceQualifiedIdentifierByIndex(int tokenIndex)
    {
        var tokens = Sense.Tokens;
        Token token = tokens.GetAt(tokenIndex)!;
        int prevIdx = tokens.PrevNonTriviaIndex(tokenIndex);
        if (prevIdx >= 0)
        {
            Token prev = tokens.GetAt(prevIdx)!;
            if (prev.Type == TokenType.ScopeResolution)
            {
                int nsIdx = tokens.PrevNonTriviaIndex(prevIdx);
                if (nsIdx >= 0)
                {
                    Token nsToken = tokens.GetAt(nsIdx)!;
                    if (nsToken.Type == TokenType.Identifier)
                    {
                        return (nsToken.Lexeme, token.Lexeme);
                    }
                }
            }
        }
        return (null, token.Lexeme);
    }

    /// <summary>
    /// Index-based: checks if token is a namespace qualifier (followed by :: and identifier),
    /// and returns the function token index after the ::.
    /// </summary>
    private bool TryGetQualifiedFunctionTokenByIndex(int tokenIndex, out int functionTokenIndex)
    {
        var tokens = Sense.Tokens;
        functionTokenIndex = -1;

        int nextIdx = tokens.NextNonTriviaIndex(tokenIndex);
        if (nextIdx < 0 || tokens.GetAt(nextIdx)!.Type != TokenType.ScopeResolution)
            return false;

        int funcIdx = tokens.NextNonTriviaIndex(nextIdx);
        if (funcIdx < 0 || tokens.GetAt(funcIdx)!.Type != TokenType.Identifier)
            return false;

        functionTokenIndex = funcIdx;
        return true;
    }

    private bool IsOnUsingLineByIndex(int tokenIndex, out string? usingPath, out Range? usingRange)
    {
        var tokens = Sense.Tokens;
        usingPath = null;
        usingRange = null;

        Token token = tokens.GetAt(tokenIndex)!;
        int line = token.Range.Start.Line;

        // Walk to start of line.
        // Use Start.Line (not End.Line): LineBreak tokens are created with End.Line = currentLine + 1,
        // so checking End.Line causes the walk to overshoot past #using.
        int cursorIdx = tokenIndex;
        while (cursorIdx > 0)
        {
            Token prev = tokens.GetAt(cursorIdx - 1)!;
            if (prev.Range.Start.Line != line) break;
            cursorIdx--;
        }

        // Find #using on this line
        int usingIdx = -1;
        for (int i = cursorIdx; i < tokens.Count; i++)
        {
            Token t = tokens.GetAt(i)!;
            if (t.Range.Start.Line != line) break;
            if (t.Lexeme == "#using") { usingIdx = i; break; }
        }
        if (usingIdx < 0) return false;

        // Collect tokens after #using up to ; or EOL
        int startIdx = tokens.NextNonWhitespaceIndex(usingIdx);
        if (startIdx < 0 || tokens.GetAt(startIdx)!.Range.Start.Line != line) return false;

        Token startToken = tokens.GetAt(startIdx)!;
        Token endToken = startToken;
        var sb = new StringBuilder();
        for (int i = startIdx; i < tokens.Count; i++)
        {
            Token t = tokens.GetAt(i)!;
            if (t.Range.Start.Line != line) break;
            if (t.Type == TokenType.Semicolon || t.Type == TokenType.LineBreak) break;
            // Include backslash tokens explicitly: IsWhitespacey() returns true for Backslash
            // (used in other contexts), but here they are path separators and must be appended.
            if (!t.IsWhitespacey() || t.Type == TokenType.Backslash) sb.Append(t.Lexeme);
            endToken = t;
        }

        usingPath = sb.ToString();
        usingRange = RangeHelper.From(startToken.Range.Start, endToken.Range.End);
        return true;
    }

    private static bool TryGetCallInfo(Token token, out Token idToken, out int activeParam)
    {
        // Delegate to shared call context finder
        return TryFindCallContext(token, out idToken, out activeParam) && idToken is not null;
    }

    /// <summary>
    /// Finds the call context from a given position in the token stream.
    /// Scans backwards to find the opening '(' of a function call, identifies the function name,
    /// and counts the active parameter index based on commas.
    /// </summary>
    /// <param name="token">Starting token (typically at cursor position)</param>
    /// <param name="idToken">Output: the identifier token of the function being called</param>
    /// <param name="activeParam">Output: the 0-based parameter index at the cursor position</param>
    /// <returns>True if a valid call context was found, false otherwise</returns>
    private static bool TryFindCallContext(Token token, out Token? idToken, out int activeParam)
    {
        idToken = null;
        activeParam = 0;

        // Scan left to find the nearest '(' that starts the current argument list
        Token? cursor = token;
        int parenDepth = 0;
        while (cursor is not null)
        {
            if (cursor.Type == TokenType.CloseParen) parenDepth++;
            if (cursor.Type == TokenType.OpenParen)
            {
                if (parenDepth == 0) break;
                parenDepth--;
            }
            if (cursor.Type == TokenType.Identifier && cursor.Next?.Type == TokenType.OpenParen && parenDepth == 0)
            {
                cursor = cursor.Next;
                break;
            }
            if (cursor.Type == TokenType.Semicolon || cursor.Type == TokenType.LineBreak)
            {
                return false; // Hit end of statement without finding '('
            }
            cursor = cursor.Previous;
        }

        if (cursor is null)
            return false; // not in a call

        // Find the identifier before this '('
        Token? id = cursor.PreviousNonTrivia();

        if (id is null || id.Type != TokenType.Identifier)
            return false;

        idToken = id;

        // Count parameter index
        // without nesting into inner parens/brackets/braces
        Token? walker = cursor.Next;
        int depthParen = 0, depthBracket = 0, depthBrace = 0;
        int index = 0;
        while (walker is not null && walker != token.Next)
        {
            if (walker.Type == TokenType.OpenParen) depthParen++;
            else if (walker.Type == TokenType.CloseParen)
            {
                if (depthParen == 0) break; // end of this call
                depthParen--;
            }
            else if (walker.Type == TokenType.OpenBracket) depthBracket++;
            else if (walker.Type == TokenType.CloseBracket && depthBracket > 0) depthBracket--;
            else if (walker.Type == TokenType.OpenBrace) depthBrace++;
            else if (walker.Type == TokenType.CloseBrace && depthBrace > 0) depthBrace--;
            else if (walker.Type == TokenType.Comma && depthParen == 0 && depthBracket == 0 && depthBrace == 0)
            {
                index++;
            }
            walker = walker.Next;
        }

        activeParam = index;
        return true;
    }

    private static string StripDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        int idx = name.IndexOf('=');
        return idx >= 0 ? name[..idx].Trim() : name.Trim();
    }

    // --- AST traversal helpers (delegates to AstTraversal) ---

    private static Range GetStmtListRange(StmtListNode body) => AstTraversal.GetStmtListRange(body);

    private static bool IsPositionInsideRange(Position pos, Range range)
    {
        int cmpStart = ComparePosition(pos, range.Start);
        int cmpEnd = ComparePosition(range.End, pos);
        return cmpStart >= 0 && cmpEnd >= 0;
    }

    private static int ComparePosition(Position a, Position b)
    {
        if (a.Line != b.Line) return a.Line.CompareTo(b.Line);
        return a.Character.CompareTo(b.Character);
    }

    private static bool TryGetRange(AstNode node, out Range range) => AstTraversal.TryGetRange(node, out range);

    private static IEnumerable<AstNode> EnumerateChildren(AstNode node) => AstTraversal.EnumerateChildren(node);

    private static IEnumerable<SwitchStmtNode> EnumerateSwitches(AstNode node) => AstTraversal.EnumerateSwitches(node);

    private static IEnumerable<FunDefnNode> EnumerateFunctions(AstNode node) => AstTraversal.EnumerateFunctions(node);

    private static IEnumerable<FunCallNode> EnumerateCalls(AstNode node) => AstTraversal.EnumerateCalls(node);

    private static IEnumerable<NamespacedMemberNode> EnumerateNamespacedMembers(AstNode node) => AstTraversal.EnumerateNamespacedMembers(node);

    private static IEnumerable<BinaryExprNode> EnumerateBinaryExprs(AstNode node) => AstTraversal.EnumerateBinaryExprs(node);

    private static void CollectIdentifiers(AstNode node, HashSet<string> into) => AstTraversal.CollectIdentifiers(node, into);
}
