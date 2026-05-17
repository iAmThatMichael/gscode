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

        var classNodes = new List<DocumentSymbol>();
        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            classNodes.Add(new DocumentSymbol
            {
                Name = kv.Key.SymbolName, Detail = kv.Key.Qualifier,
                Kind = SymbolKind.Class,
                Range = kv.Value.Range.ToRange(), SelectionRange = kv.Value.Range.ToRange(),
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
                Range = kv.Value.Range.ToRange(), SelectionRange = kv.Value.Range.ToRange()
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

        var root = new List<DocumentSymbol>(3);
        Range AnchorAt(int line) => new Range { Start = new Position { Line = line, Character = 0 }, End = new Position { Line = line, Character = 0 } };

        if (classNodes.Count > 0)
        {
            classNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Classes", Kind = SymbolKind.Namespace, Range = AnchorAt(0), SelectionRange = AnchorAt(0), Children = new Container<DocumentSymbol>(classNodes) });
        }
        if (functionNodes.Count > 0)
        {
            functionNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Functions", Kind = SymbolKind.Namespace, Range = AnchorAt(1), SelectionRange = AnchorAt(1), Children = new Container<DocumentSymbol>(functionNodes) });
        }
        if (macroNodes.Count > 0)
        {
            macroNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Macros", Kind = SymbolKind.Namespace, Range = AnchorAt(2), SelectionRange = AnchorAt(2), Children = new Container<DocumentSymbol>(macroNodes) });
        }

        sw.Stop();
        int totalSymbols = root.Sum(s => s.Children?.Count() ?? 0);
        Log.Information("DocumentSymbol finished in {ElapsedMs} ms: {Count} symbols", sw.ElapsedMilliseconds, totalSymbols);
        return new SymbolInformationOrDocumentSymbolContainer(
            root.Select(s => new SymbolInformationOrDocumentSymbol(s)));
    }

    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
        DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
