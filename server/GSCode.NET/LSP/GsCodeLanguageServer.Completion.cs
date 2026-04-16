using Serilog;
using GSCode.Parser;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace GSCode.NET.LSP;

public sealed partial class GsCodeLanguageServer
{
    // -------------------------------------------------------------------------
    // textDocument/completion + completionItem/resolve
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentCompletionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionList> CompletionAsync(CompletionParams @params, CancellationToken ct)
    {
        Log.Information("Completion request for {Uri} at {Line}:{Char}",
            @params.TextDocument.Uri, @params.Position.Line, @params.Position.Character);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null)
        {
            Log.Warning("Completion: script is NULL for {Uri}", @params.TextDocument.Uri);
            return new CompletionList { IsIncomplete = false, Items = [] };
        }
        var result = await script.GetCompletionAsync(@params.Position, ct)
            ?? new CompletionList { IsIncomplete = false, Items = [] };
        sw.Stop();
        Log.Information("Completion processed in {ElapsedMs} ms. Items: {Count}", sw.ElapsedMilliseconds, result.Items?.Length ?? 0);
        return result;
    }

    [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
    public Task<CompletionItem> ResolveCompletionItemAsync(CompletionItem item, CancellationToken ct)
        => Task.FromResult(item);

    // -------------------------------------------------------------------------
    // textDocument/signatureHelp
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentSignatureHelpName, UseSingleObjectParameterDeserialization = true)]
    public async Task<SignatureHelp?> SignatureHelpAsync(TextDocumentPositionParams @params, CancellationToken ct)
    {
        Log.Information("SignatureHelp request received, processing...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null)
        {
            sw.Stop();
            Log.Information("SignatureHelp finished in {ElapsedMs} ms: no script", sw.ElapsedMilliseconds);
            return null;
        }
        var help = await script.GetSignatureHelpAsync(@params.Position, ct);
        sw.Stop();
        Log.Information("SignatureHelp finished in {ElapsedMs} ms. Has result: {Has}", sw.ElapsedMilliseconds, help != null);
        return help;
    }
}
