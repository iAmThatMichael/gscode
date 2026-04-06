using System.Collections.ObjectModel;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;

namespace GSCode.Parser.DFA;

internal ref struct ControlFlowAnalyser(ParserIntelliSense sense, DefinitionsTable definitionsTable)
{
    public ParserIntelliSense Sense { get; } = sense;
    public DefinitionsTable DefinitionsTable { get; } = definitionsTable;

    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = new();
    public List<Tuple<ScrClass, List<ControlFlowGraph>>> ClassGraphs { get; } = new();

    public void Run()
    {
        // Evaluate & analyse all function bodies
        foreach (var (function, funDefnNode) in DefinitionsTable.LocalScopedFunctions)
        {
            // Produce a CFG for the function
            ControlFlowGraph functionGraph = ControlFlowGraph.ConstructFunctionGraph(funDefnNode, Sense);

            // Add the CFG to the list
            FunctionGraphs.Add(new(function, functionGraph));
        }

        // Evaluate & analyse all class bodies — produce independent CFGs per method
        foreach (var (scrClass, classDefnNode) in DefinitionsTable.LocalScopedClasses)
        {
            List<ControlFlowGraph> methodGraphs = new();

            foreach (AstNode child in classDefnNode.Body.Definitions)
            {
                switch (child.NodeType)
                {
                    case AstNodeType.FunctionDefinition:
                        methodGraphs.Add(ControlFlowGraph.ConstructFunctionGraph((FunDefnNode)child, Sense));
                        break;
                    case AstNodeType.Constructor:
                    case AstNodeType.Destructor:
                        methodGraphs.Add(ControlFlowGraph.ConstructStructorGraph((StructorDefnNode)child, Sense));
                        break;
                    // Member declarations (var) are handled via ScrClass.Members — no CFG needed
                }
            }

            ClassGraphs.Add(new(scrClass, methodGraphs));
        }
    }
}