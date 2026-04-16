using Serilog;
using GSCode.Parser;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Linq;

namespace GSCode.NET.LSP;

public sealed partial class GsCodeLanguageServer
{
    // -------------------------------------------------------------------------
    // textDocument/documentSymbol
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentDocumentSymbolName, UseSingleObjectParameterDeserialization = true)]
    public async Task<DocumentSymbol[]> DocumentSymbolAsync(DocumentSymbolParams @params, CancellationToken ct)
    {
        Log.Information("DocumentSymbol (outline) request received");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null || script.DefinitionsTable is null)
        {
            sw.Stop();
            Log.Information("DocumentSymbol finished in {ElapsedMs} ms: no script or no definitions", sw.ElapsedMilliseconds);
            return [];
        }
        ct.ThrowIfCancellationRequested();

        string currentPath = ScriptFileResolver.NormalizeFilePathForUri(UriHelper.GetLocalPath(@params.TextDocument.Uri));

        static string BuildFunctionLabel(string name, string? ns, string[]? parameters, string[]? flags)
        {
            string paramStr = parameters is null ? "()" : $"({string.Join(", ", parameters)})";
            string flagStr = flags is { Length: > 0 } ? $" [{string.Join(", ", flags)}]" : "";
            return $"{name}{paramStr}{flagStr}";
        }

        var classNodes = new List<DocumentSymbol>();
        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            ct.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            classNodes.Add(new DocumentSymbol
            {
                Name = kv.Key.SymbolName, Detail = kv.Key.Qualifier,
                Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Class,
                Range = kv.Value.Range.ToRange(), SelectionRange = kv.Value.Range.ToRange(),
                Children = []
            });
        }

        var functionNodes = new List<DocumentSymbol>();
        foreach (var kv in script.DefinitionsTable.GetAllFunctionLocations())
        {
            ct.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            string[]? parameters = script.DefinitionsTable.GetFunctionParameters(kv.Key.Qualifier, kv.Key.SymbolName);
            string[]? flags = script.DefinitionsTable.GetFunctionFlags(kv.Key.Qualifier, kv.Key.SymbolName);
            functionNodes.Add(new DocumentSymbol
            {
                Name = BuildFunctionLabel(kv.Key.SymbolName, kv.Key.Qualifier, parameters, flags),
                Detail = kv.Key.Qualifier,
                Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Function,
                Range = kv.Value.Range.ToRange(), SelectionRange = kv.Value.Range.ToRange()
            });
        }

        var macroNodes = new List<DocumentSymbol>();
        foreach (var m in script.MacroOutlines)
        {
            ct.ThrowIfCancellationRequested();
            macroNodes.Add(new DocumentSymbol
            {
                Name = m.Name,
                Detail = string.IsNullOrEmpty(m.SourceDisplay) ? "#define" : m.SourceDisplay,
                Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Constant,
                Range = m.Range, SelectionRange = m.Range
            });
        }

        var root = new List<DocumentSymbol>(3);
        Range AnchorAt(int line) => new Range { Start = new Position { Line = line, Character = 0 }, End = new Position { Line = line, Character = 0 } };

        if (classNodes.Count > 0)
        {
            classNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Classes", Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Namespace, Range = AnchorAt(0), SelectionRange = AnchorAt(0), Children = classNodes.ToArray() });
        }
        if (functionNodes.Count > 0)
        {
            functionNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Functions", Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Namespace, Range = AnchorAt(1), SelectionRange = AnchorAt(1), Children = functionNodes.ToArray() });
        }
        if (macroNodes.Count > 0)
        {
            macroNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Macros", Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Namespace, Range = AnchorAt(2), SelectionRange = AnchorAt(2), Children = macroNodes.ToArray() });
        }

        var symbols = root.ToArray();
        sw.Stop();
        int totalSymbols = symbols.Sum(s => s.Children?.Length ?? 0);
        Log.Information("DocumentSymbol finished in {ElapsedMs} ms: {Count} symbols", sw.ElapsedMilliseconds, totalSymbols);
        return symbols;
    }
}
