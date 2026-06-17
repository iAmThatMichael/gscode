using GSCode.Parser;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.Diagnostics;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

internal class DocumentSymbolHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : DocumentSymbolHandlerBase
{

    public override async Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        Log.Information("DocumentSymbol (outline) request received");
        var sw = Stopwatch.StartNew();
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            sw.Stop();
            Log.Information("DocumentSymbol finished in {ElapsedMs} ms: no script", sw.ElapsedMilliseconds);
            return new SymbolInformationOrDocumentSymbolContainer();
        }

        // Wait for parse to complete — SignatureAnalyser (which populates DefinitionsTable function/class
        // locations) runs during parse. Without this wait the first-open request races against parse and
        // sees an empty DefinitionsTable, showing only macros (which are populated earlier, during preprocessing).
        await script.WaitUntilParsedAsync(cancellationToken);

        if (script.DefinitionsTable is null)
        {
            sw.Stop();
            Log.Information("DocumentSymbol finished in {ElapsedMs} ms: no definitions table", sw.ElapsedMilliseconds);
            return new SymbolInformationOrDocumentSymbolContainer();
        }
        cancellationToken.ThrowIfCancellationRequested();

        string currentPath = ScriptFileResolver.NormalizeFilePathForUri(request.TextDocument.Uri.ToUri().LocalPath);

        static string BuildFunctionLabel(string name, string? ns, string[]? parameters, string[]? flags)
        {
            string paramStr = parameters is null ? "()" : $"({string.Join(", ", parameters)})";
            string flagStr = flags is { Length: > 0 } ? $" [{string.Join(", ", flags)}]" : "";
            return $"{name}{paramStr}{flagStr}";
        }

        // Collect all symbol start lines in the current file to infer body ranges.
        // GSC functions are always top-level so each symbol's body ends on the line before the next one begins.
        var allStartLines = new List<int>();
        foreach (var kv in script.DefinitionsTable.GetAllFunctionLocations())
        {
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase))
                allStartLines.Add(kv.Value.Range.StartLine);
        }
        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase))
                allStartLines.Add(kv.Value.Range.StartLine);
        }
        allStartLines.Sort();
        allStartLines = allStartLines.Distinct().ToList();

        Range InferBodyRange(int startLine)
        {
            int idx = allStartLines.BinarySearch(startLine);
            int endLine = idx >= 0 && idx + 1 < allStartLines.Count
                ? allStartLines[idx + 1] - 1
                : int.MaxValue;
            return new Range { Start = new Position { Line = startLine, Character = 0 }, End = new Position { Line = endLine, Character = int.MaxValue } };
        }

        var classNodes = new List<DocumentSymbol>();
        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            Range nameRange = kv.Value.Range.ToRange();
            classNodes.Add(new DocumentSymbol
            {
                Name = kv.Key.SymbolName, Detail = kv.Key.Qualifier,
                Kind = SymbolKind.Class,
                Range = InferBodyRange(kv.Value.Range.StartLine), SelectionRange = nameRange,
                Children = new Container<DocumentSymbol>()
            });
        }

        var functionNodes = new List<DocumentSymbol>();
        foreach (var kv in script.DefinitionsTable.GetAllFunctionLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            string[]? parameters = script.DefinitionsTable.GetFunctionParameters(kv.Key.Qualifier, kv.Key.SymbolName);
            string[]? flags = script.DefinitionsTable.GetFunctionFlags(kv.Key.Qualifier, kv.Key.SymbolName);
            functionNodes.Add(new DocumentSymbol
            {
                Name = BuildFunctionLabel(kv.Key.SymbolName, kv.Key.Qualifier, parameters, flags),
                Detail = kv.Key.Qualifier,
                Kind = SymbolKind.Function,
                Range = InferBodyRange(kv.Value.Range.StartLine), SelectionRange = kv.Value.Range.ToRange()
            });
        }

        var macroNodes = new List<DocumentSymbol>();
        foreach (var m in script.MacroOutlines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            macroNodes.Add(new DocumentSymbol
            {
                Name = m.Name,
                Detail = string.IsNullOrEmpty(m.SourceDisplay) ? "#define" : m.SourceDisplay,
                Kind = SymbolKind.Constant,
                Range = m.Range, SelectionRange = m.Range
            });
        }

        classNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        functionNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        macroNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        static Range SpanningRange(List<DocumentSymbol> nodes) => new()
        {
            Start = new Position { Line = nodes.Min(n => n.Range.Start.Line), Character = 0 },
            End = new Position { Line = nodes.Max(n => n.Range.End.Line), Character = int.MaxValue }
        };

        var root = new List<SymbolInformationOrDocumentSymbol>(3);
        if (classNodes.Count > 0)
        {
            var span = SpanningRange(classNodes);
            root.Add(new SymbolInformationOrDocumentSymbol(new DocumentSymbol
            {
                Name = "Classes", Kind = SymbolKind.Namespace,
                Range = span, SelectionRange = span,
                Children = new Container<DocumentSymbol>(classNodes)
            }));
        }
        if (functionNodes.Count > 0)
        {
            var span = SpanningRange(functionNodes);
            root.Add(new SymbolInformationOrDocumentSymbol(new DocumentSymbol
            {
                Name = "Functions", Kind = SymbolKind.Namespace,
                Range = span, SelectionRange = span,
                Children = new Container<DocumentSymbol>(functionNodes)
            }));
        }
        if (macroNodes.Count > 0)
        {
            var span = SpanningRange(macroNodes);
            root.Add(new SymbolInformationOrDocumentSymbol(new DocumentSymbol
            {
                Name = "Macros", Kind = SymbolKind.Namespace,
                Range = span, SelectionRange = span,
                Children = new Container<DocumentSymbol>(macroNodes)
            }));
        }

        sw.Stop();
        int totalSymbols = root.Sum(s => s.DocumentSymbol?.Children?.Count() ?? 0);
        Log.Information("DocumentSymbol finished in {ElapsedMs} ms: {Count} symbols", sw.ElapsedMilliseconds, totalSymbols);
        return new SymbolInformationOrDocumentSymbolContainer(root);
    }

    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
        DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
