using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Misc;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.IO;

namespace GSCode.Parser.SPA;

using SymbolKindSA = GSCode.Parser.SA.SymbolKind;

/// <summary>
/// Emits basic SPA diagnostics that run after data-flow analysis in editor mode.
/// Extracted from Script.Diagnostics.cs to follow the analyser sub-class pattern
/// used by <see cref="GSCode.Parser.DFA.ControlFlowAnalyser"/> and
/// <see cref="GSCode.Parser.DFA.DataFlowAnalyser"/>.
/// </summary>
internal ref struct ScriptDiagnosticsAnalyser(
    ScriptNode rootNode,
    ParserIntelliSense sense,
    DefinitionsTable definitionsTable,
    IReadOnlyDictionary<SymbolKey, List<Range>> references,
    ScriptLanguage language)
{
    private ScriptNode RootNode { get; } = rootNode;
    private ParserIntelliSense Sense { get; } = sense;
    private DefinitionsTable DefinitionsTable { get; } = definitionsTable;
    private IReadOnlyDictionary<SymbolKey, List<Range>> References { get; } = references;
    private ScriptLanguage Language { get; } = language;

    public void Run()
    {
        EmitUnusedParameterDiagnostics();
        EmitUnusedUsingDiagnostics();
        EmitUnusedVariableDiagnostics();
        EmitAssignOnThreadDiagnostics();
    }

    private void EmitUnusedParameterDiagnostics()
    {
        foreach (var fn in AstTraversal.EnumerateFunctions(RootNode))
        {
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
            AstTraversal.CollectIdentifiers(fn.Body, used);
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
        HashSet<string> referencedFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in References)
        {
            var key = kv.Key;
            var fLoc = DefinitionsTable.GetFunctionLocation(key.Namespace, key.Name);
            if (fLoc is not null) { referencedFiles.Add(ScriptFileResolver.NormalizeFilePathForUri(fLoc.Value.FilePath)); continue; }
            var cLoc = DefinitionsTable.GetClassLocation(key.Namespace, key.Name);
            if (cLoc is not null) { referencedFiles.Add(ScriptFileResolver.NormalizeFilePathForUri(cLoc.Value.FilePath)); continue; }
        }

        foreach (var depNode in RootNode.Dependencies)
        {
            string rel = depNode.Path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string expectedSuffix = Path.DirectorySeparatorChar + rel + Language.ToExtension();

            bool anyFromThisUsing = false;
            foreach (var referenced in referencedFiles)
            {
                if (referenced.Contains(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    anyFromThisUsing = true;
                    break;
                }
            }

            if (!anyFromThisUsing)
            {
                // Don't flag the using as unused if the referenced file contains an autoexec
                // function — autoexec runs purely because the file is #using'd, with no explicit
                // call site for the analyser to find.
                if (DefinitionsTable.AnyFunctionInDependencyHasFlag(expectedSuffix, "autoexec"))
                    continue;

                Sense.AddSpaDiagnostic(depNode.Range, GSCErrorCodes.UnusedUsing, depNode.Path);
            }
        }
    }

    private void EmitUnusedVariableDiagnostics()
    {
        foreach (var fn in AstTraversal.EnumerateFunctions(RootNode))
        {
            Dictionary<string, int> usageCounts = new(StringComparer.OrdinalIgnoreCase);
            AstTraversal.CollectIdentifierCounts(fn.Body, usageCounts);

            foreach (var node in AstTraversal.EnumerateChildren(fn.Body))
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
                    if (count <= 1)
                    {
                        Sense.AddSpaDiagnostic(id.Range, GSCErrorCodes.UnusedVariable, name);
                    }
                }
            }
        }
    }

    private void EmitAssignOnThreadDiagnostics()
    {
        // Early exit: Check if there are any thread calls in the entire file first
        bool hasAnyThreadCalls = false;
        foreach (var node in AstTraversal.EnumerateChildren(RootNode))
        {
            if (ContainsThreadCallQuickCheck(node))
            {
                hasAnyThreadCalls = true;
                break;
            }
        }

        if (!hasAnyThreadCalls)
        {
            return;
        }

        var cache = new Dictionary<AstNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var fn in AstTraversal.EnumerateFunctions(RootNode))
        {
            foreach (var bin in AstTraversal.EnumerateBinaryExprs(fn.Body))
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
            foreach (var child in AstTraversal.EnumerateChildren(node))
            {
                if (ContainsThreadCallCached(child, cache)) { result = true; break; }
            }
        }
        cache[node] = result;
        return result;
    }

    private static bool ContainsThreadCallQuickCheck(AstNode node)
    {
        if (node is PrefixExprNode pe && pe.Operation == TokenType.Thread)
        {
            return true;
        }
        foreach (var child in AstTraversal.EnumerateChildren(node))
        {
            if (child is PrefixExprNode pec && pec.Operation == TokenType.Thread)
            {
                return true;
            }
        }
        return false;
    }
}

