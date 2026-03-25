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

internal ref struct DataFlowAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, List<Tuple<ScrClass, List<ControlFlowGraph>>> classGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable, ScriptAnalyserData? apiData = null, string? currentNamespace = null, HashSet<string>? knownNamespaces = null, string? fileName = null, DefinitionsTable? definitionsTable = null)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public List<Tuple<ScrClass, List<ControlFlowGraph>>> ClassGraphs { get; } = classGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;
    public ScriptAnalyserData? ApiData { get; } = apiData;
    public string? CurrentNamespace { get; } = currentNamespace;
    public HashSet<string>? KnownNamespaces { get; } = knownNamespaces;
    public string? FileName { get; } = fileName;
    public DefinitionsTable? DefinitionsTable { get; } = definitionsTable;

    public void Run()
    {
        ReachingDefinitionsAnalyser reachingDefinitionsAnalyser = new(FunctionGraphs, ClassGraphs, Sense, ExportedSymbolTable, ApiData, CurrentNamespace, KnownNamespaces, FileName, DefinitionsTable);
        reachingDefinitionsAnalyser.Run();

        SemanticSenseGenerator semanticSenseGenerator = new(FunctionGraphs, Sense, ExportedSymbolTable, reachingDefinitionsAnalyser);
        semanticSenseGenerator.Run();
    }
}