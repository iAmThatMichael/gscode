using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Util;

/// <summary>
/// Shared static helpers for traversing AST nodes.
/// Used by Script and analyser sub-classes.
/// </summary>
internal static class AstTraversal
{
    public static IEnumerable<AstNode> EnumerateChildren(AstNode node)
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

    public static IEnumerable<FunDefnNode> EnumerateFunctions(AstNode node)
    {
        if (node is FunDefnNode fn) yield return fn;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var f in EnumerateFunctions(child)) yield return f;
        }
    }

    public static IEnumerable<BinaryExprNode> EnumerateBinaryExprs(AstNode node)
    {
        if (node is BinaryExprNode b) yield return b;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateBinaryExprs(child)) yield return c;
        }
    }

    public static IEnumerable<SwitchStmtNode> EnumerateSwitches(AstNode node)
    {
        if (node is SwitchStmtNode s) yield return s;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var x in EnumerateSwitches(child)) yield return x;
        }
    }

    public static IEnumerable<FunCallNode> EnumerateCalls(AstNode node)
    {
        if (node is FunCallNode f) yield return f;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateCalls(child)) yield return c;
        }
    }

    public static IEnumerable<NamespacedMemberNode> EnumerateNamespacedMembers(AstNode node)
    {
        if (node is NamespacedMemberNode n) yield return n;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateNamespacedMembers(child)) yield return c;
        }
    }

    public static void CollectIdentifiers(AstNode node, HashSet<string> into)
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

    public static void CollectIdentifierCounts(AstNode node, Dictionary<string, int> into)
    {
        if (node is IdentifierExprNode id)
        {
            if (!into.TryGetValue(id.Identifier, out int c)) c = 0;
            into[id.Identifier] = c + 1;
        }
        foreach (var child in EnumerateChildren(node))
        {
            CollectIdentifierCounts(child, into);
        }
    }

    public static Range GetStmtListRange(StmtListNode body)
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

    public static bool TryGetRange(AstNode node, out Range range)
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
}
