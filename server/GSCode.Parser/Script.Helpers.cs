using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using GSCode.Parser.Util;
using System.Runtime.CompilerServices;
using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

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
        for (int i = startIdx; i < tokens.Count; i++)
        {
            Token t = tokens.GetAt(i)!;
            if (t.Range.Start.Line != line) break;
            if (t.Type == TokenType.Semicolon || t.Type == TokenType.LineBreak) break;
            endToken = t;
        }

        var sb = new StringBuilder();
        for (int i = startIdx; ; i++)
        {
            Token t = tokens.GetAt(i)!;
            if (t.Range.Start.Line != line) break;
            if (t.Type == TokenType.Semicolon || t.Type == TokenType.LineBreak) break;
            // Include backslash tokens explicitly: IsWhitespacey() returns true for Backslash
            // (used in other contexts), but here they are path separators and must be appended.
            if (!t.IsWhitespacey() || t.Type == TokenType.Backslash) sb.Append(t.Lexeme);
            if (ReferenceEquals(t, endToken)) break;
        }

        usingPath = sb.ToString();
        usingRange = RangeHelper.From(startToken.Range.Start, endToken.Range.End);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFunctionPointerCallIdentifier(Token identifier)
    {
        // Pattern: [[ identifier ]]( ... )
        // Check immediate surrounding tokens ignoring trivia
        Token? prev1 = identifier.PreviousNonTrivia();
        if (prev1?.Type != TokenType.OpenBracket) return false;
        Token? prev2 = prev1.PreviousNonTrivia();
        if (prev2?.Type != TokenType.OpenBracket) return false;
        Token? next1 = identifier.NextNonTrivia();
        if (next1?.Type != TokenType.CloseBracket) return false;
        Token? next2 = next1.NextNonTrivia();
        if (next2?.Type != TokenType.CloseBracket) return false;
        Token? next3 = next2.NextNonTrivia();
        if (next3?.Type != TokenType.OpenParen) return false;
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
        Token? id = cursor.Previous;
        while (id is not null && (id.IsWhitespacey() || id.IsComment())) 
            id = id.Previous;

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

    // --- AST traversal helpers ---

    private static Range GetStmtListRange(StmtListNode body)
    {
        if (body.Statements.Count == 0)
        {
            return RangeHelper.From(0, 0, 0, 0);
        }
        Range? start = null;
        Range? end = null;
        foreach (var st in body.Statements)
        {
            if (TryGetRange(st, out var r))
            {
                if (start is null) start = r;
                end = r;
            }
        }
        if (start is null || end is null)
        {
            return RangeHelper.From(0, 0, 0, 0);
        }
        return RangeHelper.From(start!.Start, end!.End);
    }

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

    private static bool TryGetRange(AstNode node, out Range range)
    {
        // Fast paths for nodes that carry a Range
        switch (node)
        {
            case ExprNode e:
                range = e.Range; return true;
            case ControlFlowActionNode cfan:
                range = cfan.Range; return true;
            case ConstStmtNode cst:
                range = cst.Range; return true;
            case ExprStmtNode es when es.Expr is not null:
                range = es.Expr.Range; return true;
            case ReturnStmtNode rt when rt.Value is not null:
                range = rt.Value.Range; return true;
            case ArgsListNode al:
                range = al.Range; return true;
        }

        // Composite statements: compute union of child ranges
        bool ok = false;
        Range start = default, end = default;
        void Acc(Range r)
        {
            if (!ok) { start = r; end = r; ok = true; }
            else { start = RangeHelper.From(start.Start, r.Start); end = RangeHelper.From(end.Start, r.End); }
        }

        switch (node)
        {
            case IfStmtNode iff:
                if (iff.Condition is not null && TryGetRange(iff.Condition, out var rc)) Acc(rc);
                if (iff.Then is not null && TryGetRange(iff.Then, out var rt)) Acc(rt);
                if (iff.Else is not null && TryGetRange(iff.Else, out var re)) Acc(re);
                break;
            case DoWhileStmtNode dw:
                if (dw.Then is not null && TryGetRange(dw.Then, out var rdw)) Acc(rdw);
                if (dw.Condition is not null && TryGetRange(dw.Condition, out var cdw)) Acc(cdw);
                break;
            case WhileStmtNode wl:
                if (wl.Then is not null && TryGetRange(wl.Then, out var rwl)) Acc(rwl);
                if (wl.Condition is not null && TryGetRange(wl.Condition, out var cwl)) Acc(cwl);
                break;
            case ForStmtNode fr:
                if (fr.Init is not null && TryGetRange(fr.Init, out var ri)) Acc(ri);
                if (fr.Condition is not null && TryGetRange(fr.Condition, out var rc2)) Acc(rc2);
                if (fr.Increment is not null && TryGetRange(fr.Increment, out var rinc)) Acc(rinc);
                if (fr.Then is not null && TryGetRange(fr.Then, out var rthen)) Acc(rthen);
                break;
            case ForeachStmtNode fe:
                if (fe.Collection is not null && TryGetRange(fe.Collection, out var rcol)) Acc(rcol);
                if (fe.Then is not null && TryGetRange(fe.Then, out var rfe)) Acc(rfe);
                break;
            case FunDevBlockNode fdb:
                var rbody = GetStmtListRange(fdb.Body); Acc(rbody);
                break;
            case StmtListNode sl:
                foreach (var st in sl.Statements)
                {
                    if (TryGetRange(st, out var rs)) Acc(rs);
                }
                break;
            case SwitchStmtNode sw:
                if (sw.Expression is not null && TryGetRange(sw.Expression, out var rexp)) Acc(rexp);
                if (TryGetRange(sw.Cases, out var rcases)) Acc(rcases);
                break;
            case CaseListNode cl:
                foreach (var cs in cl.Cases)
                {
                    if (TryGetRange(cs, out var rcs)) Acc(rcs);
                }
                break;
            case CaseStmtNode cs:
                foreach (var lbl in cs.Labels)
                {
                    if (TryGetRange(lbl, out var rlbl)) Acc(rlbl);
                }
                var rb = GetStmtListRange(cs.Body); Acc(rb);
                break;
            case CaseLabelNode cln when cln.Value is not null:
                range = cln.Value.Range; return true;
            case ClassBodyListNode cbl:
                foreach (var d in cbl.Definitions)
                {
                    if (TryGetRange(d, out var rd)) Acc(rd);
                }
                break;
        }

        if (ok)
        {
            range = RangeHelper.From(start.Start, end.End);
            return true;
        }
        range = default;
        return false;
    }

    private static IEnumerable<AstNode> EnumerateChildren(AstNode node)
    {
        switch (node)
        {
            case ScriptNode s:
                foreach (var d in s.Dependencies) yield return d;
                foreach (var sd in s.ScriptDefns) yield return sd; break;
            case ClassBodyListNode cbl:
                foreach (var d in cbl.Definitions) yield return d; break;
            case StmtListNode sl:
                foreach (var st in sl.Statements) yield return st; break;
            case IfStmtNode iff:
                if (iff.Condition is not null) yield return iff.Condition;
                if (iff.Then is not null) yield return iff.Then;
                if (iff.Else is not null) yield return iff.Else; break;
            case ReservedFuncStmtNode rfs:
                if (rfs.Expr is not null) yield return rfs.Expr; break;
            case ConstStmtNode cst:
                if (cst.Value is not null) yield return cst.Value; break;
            case ExprStmtNode es:
                if (es.Expr is not null) yield return es.Expr; break;
            case DoWhileStmtNode dw:
                if (dw.Condition is not null) yield return dw.Condition; if (dw.Then is not null) yield return dw.Then; break;
            case WhileStmtNode wl:
                if (wl.Condition is not null) yield return wl.Condition; if (wl.Then is not null) yield return wl.Then; break;
            case ForStmtNode fr:
                if (fr.Init is not null) yield return fr.Init; if (fr.Condition is not null) yield return fr.Condition; if (fr.Increment is not null) yield return fr.Increment; if (fr.Then is not null) yield return fr.Then; break;
            case ForeachStmtNode fe:
                if (fe.Collection is not null) yield return fe.Collection; if (fe.Then is not null) yield return fe.Then; break;
            case ReturnStmtNode rt:
                if (rt.Value is not null) yield return rt.Value; break;
            case DefnDevBlockNode db:
                foreach (var d in db.Definitions) yield return d; break;
            case FunDevBlockNode fdb:
                yield return fdb.Body; break;
            case SwitchStmtNode sw:
                if (sw.Expression is not null) yield return sw.Expression; yield return sw.Cases; break;
            case CaseListNode cl:
                foreach (var c in cl.Cases) yield return c; break;
            case CaseStmtNode cs:
                foreach (var l in cs.Labels) yield return l; yield return cs.Body; break;
            case CaseLabelNode cln:
                if (cln.Value is not null) yield return cln.Value; break;
            case FunDefnNode fd:
                yield return fd.Parameters; yield return fd.Body; break;
            case ParamListNode pl:
                foreach (var p in pl.Parameters) yield return p; break;
            case ParamNode pn:
                if (pn.Default is not null) yield return pn.Default; break;
            case VectorExprNode vx:
                yield return vx.X; if (vx.Y is not null) yield return vx.Y; if (vx.Z is not null) yield return vx.Z; break;
            case TernaryExprNode te:
                yield return te.Condition; if (te.Then is not null) yield return te.Then; if (te.Else is not null) yield return te.Else; break;
            case BinaryExprNode be:
                if (be.Left is not null) yield return be.Left; if (be.Right is not null) yield return be.Right; break;
            case PrefixExprNode pe:
                yield return pe.Operand; break;
            case PostfixExprNode pxe:
                yield return pxe.Operand; break;
            case ConstructorExprNode:
                break;
            case MethodCallNode mc:
                if (mc.Target is not null) yield return mc.Target; yield return mc.Arguments; break;
            case FunCallNode fc:
                if (fc.Function is not null) yield return fc.Function; yield return fc.Arguments; break;
            case NamespacedMemberNode nm:
                yield return nm.Namespace; yield return nm.Member; break;
            case ArgsListNode al:
                foreach (var a in al.Arguments) { if (a is not null) yield return a; }
                break;
            case ArrayIndexNode ai:
                yield return ai.Array; if (ai.Index is not null) yield return ai.Index; break;
            case CalledOnNode con:
                yield return con.On; yield return con.Call; break;
            default:
                break;
        }
    }

    private static IEnumerable<SwitchStmtNode> EnumerateSwitches(AstNode node)
    {
        if (node is SwitchStmtNode s) yield return s;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var x in EnumerateSwitches(child)) yield return x;
        }
    }

    // Enumerators
    private static IEnumerable<FunDefnNode> EnumerateFunctions(AstNode node)
    {
        if (node is FunDefnNode fn) yield return fn;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var f in EnumerateFunctions(child)) yield return f;
        }
    }

    private static IEnumerable<FunCallNode> EnumerateCalls(AstNode node)
    {
        if (node is FunCallNode f) yield return f;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateCalls(child)) yield return c;
        }
    }

    private static IEnumerable<NamespacedMemberNode> EnumerateNamespacedMembers(AstNode node)
    {
        if (node is NamespacedMemberNode n) yield return n;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateNamespacedMembers(child)) yield return c;
        }
    }

    private static IEnumerable<BinaryExprNode> EnumerateBinaryExprs(AstNode node)
    {
        if (node is BinaryExprNode b) yield return b;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateBinaryExprs(child)) yield return c;
        }
    }

    private static void CollectIdentifiers(AstNode node, HashSet<string> into)
    {
        switch (node)
        {
            case IdentifierExprNode id:
                into.Add(id.Identifier);
                break;
        }
        foreach (var child in EnumerateChildren(node))
        {
            CollectIdentifiers(child, into);
        }
    }
}
