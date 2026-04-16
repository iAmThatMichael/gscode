using Serilog;
using GSCode.Parser;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using System.Linq;

namespace GSCode.NET.LSP;

public sealed partial class GsCodeLanguageServer
{
    // -------------------------------------------------------------------------
    // workspace/didChangeConfiguration
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.WorkspaceDidChangeConfigurationName, UseSingleObjectParameterDeserialization = true)]
    public void DidChangeConfiguration(DidChangeConfigurationParams @params)
    {
        try
        {
            var settings = @params.Settings as JToken;
            var gscodeSection = settings?["gscode"];
            if (gscodeSection is null) return;

            var indexingModeStr = gscodeSection["workspaceIndexingMode"]?.Value<string>();
            if (!string.IsNullOrEmpty(indexingModeStr))
            {
                var mode = indexingModeStr.ToLowerInvariant() switch
                {
                    "full"    => GSCode.Parser.Configuration.IndexingMode.Full,
                    "partial" => GSCode.Parser.Configuration.IndexingMode.Partial,
                    _         => GSCode.Parser.Configuration.IndexingMode.Off
                };
                var currentOpts = GSCode.Parser.Configuration.CompletionOptions.Current;
                if (currentOpts is not null) currentOpts.WorkspaceIndexingMode = mode;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to process configuration change");
        }
    }

    // -------------------------------------------------------------------------
    // workspace/didChangeWatchedFiles
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.WorkspaceDidChangeWatchedFilesName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidChangeWatchedFilesAsync(DidChangeWatchedFilesParams @params, CancellationToken ct)
    {
        foreach (var change in @params.Changes)
        {
            var path = UriHelper.GetLocalPath(change.Uri);
            if (IsScriptFile(path))
            {
                switch (change.FileChangeType)
                {
                    case FileChangeType.Created: Log.Information("Script file created: {Path}", path); break;
                    case FileChangeType.Changed: Log.Information("Script file modified externally: {Path}", path); break;
                    case FileChangeType.Deleted: Log.Information("Script file deleted: {Path}", path); break;
                }
            }
        }
        bool hasScriptChange = @params.Changes.Any(c => IsScriptFile(UriHelper.GetLocalPath(c.Uri)));
        if (hasScriptChange)
        {
            Log.Information("Watched script file changed; re-parsing all open editors");
            await _scriptManager.ReparseAllOpenEditorsAsync(ct);
        }
    }

    // -------------------------------------------------------------------------
    // textDocument/codeAction + codeAction/resolve
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentCodeActionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CodeAction[]> CodeActionAsync(CodeActionParams @params, CancellationToken ct)
    {
        var handler = new Handlers.CodeActionHandler(_scriptManager);
        return await handler.GetCodeActionsAsync(@params, ct);
    }

    [JsonRpcMethod(Methods.CodeActionResolveName, UseSingleObjectParameterDeserialization = true)]
    public Task<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct)
        => Task.FromResult(action);

    // -------------------------------------------------------------------------
    // textDocument/foldingRange
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentFoldingRangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task<FoldingRange[]> FoldingRangeAsync(FoldingRangeParams @params, CancellationToken ct)
    {
        Log.Information("Folding range request received, processing...");
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return [];
        var ranges = await script.GetFoldingRangesAsync(ct);
        var result = ranges.ToArray();
        Log.Information("Folding range request processed. FoldingRanges: {Count}", result.Length);
        return result;
    }
}
