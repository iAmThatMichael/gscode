using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Misc;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.IO;

namespace GSCode.Parser;

using SymbolKindSA = GSCode.Parser.SA.SymbolKind;

public partial class Script
{
    // ===== SPA helpers =====

    private void EmitUnusedParameterDiagnostics()
    {
        if (RootNode is null) return;
        // Traverse functions
        foreach (var fn in EnumerateFunctions(RootNode))
        {
            // collect used identifiers in function body
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
            CollectIdentifiers(fn.Body, used);
            foreach (var p in fn.Parameters.Parameters)
            {
                if (p.Name is null) continue;
                string paramName = p.Name.Lexeme;
                if (!used.Contains(paramName))
                {
                    Sense.AddSpaDiagnostic(p.Name.Range, GSCErrorCodes.UnusedParameter, paramName);
                }
            }
        }
    }

    private void EmitUnusedUsingDiagnostics()
    {
        if (RootNode is null || DefinitionsTable is null) return;

        // Map referenced file paths
        HashSet<string> referencedFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _references)
        {
            var key = kv.Key;
            var fLoc = DefinitionsTable.GetFunctionLocation(key.Namespace, key.Name);
            if (fLoc is not null) { referencedFiles.Add(ScriptFileResolver.NormalizeFilePathForUri(fLoc.Value.FilePath)); continue; }
            var cLoc = DefinitionsTable.GetClassLocation(key.Namespace, key.Name);
            if (cLoc is not null) { referencedFiles.Add(ScriptFileResolver.NormalizeFilePathForUri(cLoc.Value.FilePath)); continue; }
        }

        // For each using directive, if no referenced symbol comes from that dependency file, mark unused
        foreach (var depNode in RootNode.Dependencies)
        {
            // Build expected suffix: ..\scripts\<depNode.Path>.<LanguageId>
            string rel = depNode.Path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string expectedSuffix = Path.DirectorySeparatorChar + rel + "." + LanguageId;

            bool anyFromThisUsing = false;
            foreach (var referenced in referencedFiles)
            {
                // referenced already normalized
                if (referenced.Contains(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    anyFromThisUsing = true;
                    break;
                }
            }

            if (!anyFromThisUsing)
            {
                Sense.AddSpaDiagnostic(depNode.Range, GSCErrorCodes.UnusedUsing, depNode.Path);
            }
        }
    }

    private void EmitUnusedVariableDiagnostics()
    {
        if (RootNode is null) return;
        foreach (var fn in EnumerateFunctions(RootNode))
        {
            // Count all identifier occurrences within the function body
            Dictionary<string, int> usageCounts = new(StringComparer.OrdinalIgnoreCase);
            CollectIdentifierCounts(fn.Body, usageCounts);

            // Single pass through function body to flag unused consts and assignments
            foreach (var node in EnumerateChildren(fn.Body))
            {
                if (node is ConstStmtNode cst)
                {
                    string name = cst.Identifier;
                    usageCounts.TryGetValue(name, out int count);
                    if (count == 0)
                    {
                        Sense.AddSpaDiagnostic(cst.Range, GSCErrorCodes.UnusedVariable, name);
                    }
                    continue;
                }

                if (node is ExprStmtNode es && es.Expr is BinaryExprNode be && be.Operation == TokenType.Assign && be.Left is IdentifierExprNode id)
                {
                    string name = id.Identifier;
                    usageCounts.TryGetValue(name, out int count);
                    // If the only occurrence is this defining assignment (LHS), consider it unused
                    if (count <= 1)
                    {
                        Sense.AddSpaDiagnostic(id.Range, GSCErrorCodes.UnusedVariable, name);
                    }
                }
            }
        }
    }

    private static void CollectIdentifierCounts(AstNode node, Dictionary<string, int> into)
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

    private void EmitSwitchCaseDiagnostics()
    {
        // Switch case diagnostics (duplicate labels, fallthrough, multiple defaults)
        // are now handled in ControlFlowAnalyser (CFA).
        // This method is kept as a placeholder for any future switch-specific diagnostics
        // that don't fit in CFA.
    }

    private static bool ContainsThreadCall(AstNode node)
    {
        if (node is PrefixExprNode pe && pe.Operation == TokenType.Thread)
        {
            return true;
        }
        if (node is CalledOnNode con)
        {
            // thread appears on the call side for patterns like self thread foo();
            if (ContainsThreadCall(con.Call)) return true;
            // also traverse 'on' to be thorough
            if (ContainsThreadCall(con.On)) return true;
            return false;
        }
        foreach (var child in EnumerateChildren(node))
        {
            if (ContainsThreadCall(child)) return true;
        }
        return false;
    }

    private static bool ContainsThreadCallCached(AstNode node, Dictionary<AstNode, bool> cache)
    {
        if (cache.TryGetValue(node, out var v)) return v;
        bool result;
        if (node is PrefixExprNode pe && pe.Operation == TokenType.Thread)
        {
            result = true;
        }
        else if (node is CalledOnNode con)
        {
            result = ContainsThreadCallCached(con.Call, cache) || ContainsThreadCallCached(con.On, cache);
        }
        else
        {
            result = false;
            foreach (var child in EnumerateChildren(node))
            {
                if (ContainsThreadCallCached(child, cache)) { result = true; break; }
            }
        }
        cache[node] = result;
        return result;
    }

    private void EmitAssignOnThreadDiagnostics()
    {
        if (RootNode is null) return;

        // Early exit: Check if there are any thread calls in the entire file first
        // This avoids expensive nested enumeration if there's nothing to check
        bool hasAnyThreadCalls = false;
        foreach (var node in EnumerateChildren(RootNode))
        {
            if (ContainsThreadCallQuickCheck(node))
            {
                hasAnyThreadCalls = true;
                break;
            }
        }

        if (!hasAnyThreadCalls)
        {
            return; // No thread calls in file, skip expensive analysis
        }

        // Proceed with full analysis only if thread calls exist
        var cache = new Dictionary<AstNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var fn in EnumerateFunctions(RootNode))
        {
            foreach (var bin in EnumerateBinaryExprs(fn.Body))
            {
                if (bin.Operation == TokenType.Assign && bin.Right is not null)
                {
                    if (ContainsThreadCallCached(bin.Right, cache))
                    {
                        Sense.AddSpaDiagnostic(bin.Range, GSCErrorCodes.AssignOnThreadedFunction);
                    }
                }
            }
        }
    }

    // Quick shallow check for thread token without deep recursion
    private static bool ContainsThreadCallQuickCheck(AstNode node)
    {
        if (node is PrefixExprNode pe && pe.Operation == TokenType.Thread)
        {
            return true;
        }
        // Only check immediate children, not deep recursion
        foreach (var child in EnumerateChildren(node))
        {
            if (child is PrefixExprNode pec && pec.Operation == TokenType.Thread)
            {
                return true;
            }
        }
        return false;
    }
}
