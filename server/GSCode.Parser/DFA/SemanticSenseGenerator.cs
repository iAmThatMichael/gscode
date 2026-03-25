using GSCode.Parser.CFA;
using GSCode.Parser.Data;

namespace GSCode.Parser.DFA;

internal ref struct SemanticSenseGenerator(
    List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, 
    ParserIntelliSense sense, 
    Dictionary<string, IExportedSymbol> exportedSymbolTable,
    TypeFlowAnalyser typeFlowAnalyser)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;

    private readonly TypeFlowAnalyser _typeFlowAnalyser = typeFlowAnalyser;

    public void Run()
    {
        
    }
}

