using Serilog;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Linq;

namespace GSCode.NET.LSP;

public sealed partial class GsCodeLanguageServer
{
    // -------------------------------------------------------------------------
    // textDocument/did*
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentDidOpenName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidOpenAsync(DidOpenTextDocumentParams @params, CancellationToken ct)
    {
        Log.Information("Document opened");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var diags = await _scriptManager.AddEditorAsync(@params.TextDocument, ct);
        await PublishDiagnosticsAsync(@params.TextDocument.Uri, diags, ct);
        sw.Stop();
        Log.Information("Document open processed in {ElapsedMs} ms with {DiagCount} diagnostics", sw.ElapsedMilliseconds, diags.Count());
    }

    [JsonRpcMethod(Methods.TextDocumentDidChangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidChangeAsync(DidChangeTextDocumentParams @params, CancellationToken ct)
    {
        var changeType = @params.ContentChanges.Any(c => c.Range == null) ? "Full" : "Incremental";
        var changeCount = @params.ContentChanges.Length;
        Log.Information("Document changed ({ChangeType}, {ChangeCount} change(s))", changeType, changeCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var versionedId = new VersionedTextDocumentIdentifier
        {
            Uri = @params.TextDocument.Uri,
            Version = @params.TextDocument.Version
        };
        var diags = await _scriptManager.UpdateEditorAsync(versionedId, @params.ContentChanges, ct);
        await PublishDiagnosticsAsync(@params.TextDocument.Uri, diags, ct);
        sw.Stop();
        Log.Information("Document change processed in {ElapsedMs} ms with {DiagCount} diagnostics", sw.ElapsedMilliseconds, diags.Count());
    }

    [JsonRpcMethod(Methods.TextDocumentDidCloseName, UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams @params)
    {
        Log.Information("Document closed");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _scriptManager.RemoveEditor(@params.TextDocument);
        sw.Stop();
        Log.Information("Document close processed in {ElapsedMs} ms", sw.ElapsedMilliseconds);
    }

    [JsonRpcMethod(Methods.TextDocumentDidSaveName, UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams @params)
    {
        if (!GSCode.Parser.Configuration.CompletionConfiguration.AllowRawFolderWrites)
        {
            string path = UriHelper.GetLocalPath(@params.TextDocument.Uri);
            if (IsInProtectedRawFolder(path))
            {
                Log.Warning("File saved in protected raw folder: {Path}. Consider setting gscode.allowRawFolderWrites to false or working in a separate mod directory.", path);
                _ = _rpc.NotifyWithParameterObjectAsync(Methods.WindowShowMessageName, new ShowMessageParams
                {
                    MessageType = MessageType.Error,
                    Message = "You are editing a file in a protected raw folder. Consider working in a separate mod directory to avoid modifying vanilla game files."
                });
            }
        }
    }
}
