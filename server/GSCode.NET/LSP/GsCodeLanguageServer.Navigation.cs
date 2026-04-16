using Serilog;
using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Linq;

namespace GSCode.NET.LSP;

public sealed partial class GsCodeLanguageServer
{
    // -------------------------------------------------------------------------
    // textDocument/hover
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentHoverName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams @params, CancellationToken ct)
    {
        Log.Information("Hover request received, processing...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) { sw.Stop(); return null; }
        var result = await script.GetHoverAsync(@params.Position, ct);
        sw.Stop();
        Log.Information("Hover processed in {ElapsedMs} ms. Has result: {Has}", sw.ElapsedMilliseconds, result != null);
        return result;
    }

    // -------------------------------------------------------------------------
    // textDocument/definition
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentDefinitionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Location?> DefinitionAsync(TextDocumentPositionParams @params, CancellationToken ct)
    {
        string docPath = UriHelper.GetLocalPath(@params.TextDocument.Uri);
        Log.Information("Definition request from {DocumentPath} at position {Position}", docPath, @params.Position);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null)
        {
            sw.Stop();
            Log.Information("Definition finished in {ElapsedMs} ms: no script for {DocumentPath}", sw.ElapsedMilliseconds, docPath);
            return null;
        }
        var result = await script.GetDefinitionAsync(@params.Position, ct);
        sw.Stop();
        if (result is not null)
            Log.Information("Definition resolved in {ElapsedMs} ms from {DocumentPath}: {Uri}:{Range}", sw.ElapsedMilliseconds, docPath, result.Uri, result.Range);
        else
            Log.Information("Definition finished in {ElapsedMs} ms from {DocumentPath}: not found", sw.ElapsedMilliseconds, docPath);
        return result;
    }

    // -------------------------------------------------------------------------
    // textDocument/references
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentReferencesName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Location[]> ReferencesAsync(ReferenceParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return [];

        // Try local variable references first
        var localRefs = await script.GetLocalVariableReferencesAsync(
            @params.Position, @params.Context?.IncludeDeclaration == true, ct);
        if (localRefs.Count > 0)
            return localRefs.Select(r => new Location { Uri = @params.TextDocument.Uri, Range = r }).ToArray();

        var qid = await script.GetQualifiedIdentifierAtAsync(@params.Position, ct);
        if (qid is null) return [];

        string ns = qid.Value.qualifier ?? (script.DefinitionsTable?.CurrentNamespace ?? "");
        string name = qid.Value.name;

        var keys = new[]
        {
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Function, ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Method,   ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Class,    ns, name)
        };

        var results = new List<Location>();
        foreach (var loaded in _scriptManager.GetLoadedScripts())
        {
            foreach (var key in keys)
            {
                if (loaded.Script.References.TryGetValue(key, out var ranges))
                    foreach (var r in ranges)
                        results.Add(new Location { Uri = loaded.Uri, Range = r });
            }

            if (@params.Context?.IncludeDeclaration == true && loaded.Script.DefinitionsTable is not null)
            {
                foreach (var key in keys)
                {
                    var loc = loaded.Script.DefinitionsTable.GetFunctionLocation(key.Namespace, key.Name)
                           ?? loaded.Script.DefinitionsTable.GetClassLocation(key.Namespace, key.Name);
                    if (loc is not null)
                    {
                        string norm = ScriptFileResolver.NormalizeFilePathForUri(loc.Value.FilePath);
                        results.Add(new Location { Uri = new Uri(norm), Range = loc.Value.Range.ToRange() });
                    }
                }
            }
        }

        return results.ToArray();
    }

    // -------------------------------------------------------------------------
    // textDocument/documentHighlight
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentDocumentHighlightName, UseSingleObjectParameterDeserialization = true)]
    public async Task<DocumentHighlight[]> DocumentHighlightAsync(TextDocumentPositionParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return [];

        var ranges = await script.GetLocalVariableReferencesAsync(@params.Position, includeDeclaration: true, ct);
        return ranges.Select(r => new DocumentHighlight { Range = r, Kind = DocumentHighlightKind.Text }).ToArray();
    }
}
