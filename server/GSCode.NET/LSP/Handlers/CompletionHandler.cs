using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.Diagnostics;

namespace GSCode.NET.LSP.Handlers;

internal class CompletionHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : CompletionHandlerBase
{
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
        => Task.FromResult(request);

    public override async Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        Log.Information("Completion request for {Uri} at {Line}:{Char}",
            request.TextDocument.Uri, request.Position.Line, request.Position.Character);
        var sw = Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            Log.Warning("Completion: script is NULL for {Uri}", request.TextDocument.Uri);
            return new CompletionList();
        }
        var result = await script.GetCompletionAsync(request.Position, cancellationToken)
            ?? new CompletionList();
        sw.Stop();
        Log.Information("Completion processed in {ElapsedMs} ms. Items: {Count}",
            sw.ElapsedMilliseconds, result.Items?.Count() ?? 0);
        return result;
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _documentSelector,
            TriggerCharacters = new List<string> { ".", ":", "#", "(", ",", "\\", "/" },
            ResolveProvider = true
        };
}
