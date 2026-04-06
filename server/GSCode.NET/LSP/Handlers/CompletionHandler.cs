using GSCode.Parser;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace GSCode.NET.LSP.Handlers;

internal class CompletionHandler(ILanguageServerFacade facade,
    ScriptManager scriptManager,
    ILogger<CompletionHandler> logger,
    TextDocumentSelector documentSelector) : CompletionHandlerBase
{
    private readonly ILanguageServerFacade _facade = facade;
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly ILogger<CompletionHandler> _logger = logger;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    // Implement resolve to avoid NotImplementedException when client requests item resolution
    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        // If needed, enrich the item using request.Data here.
        // For now, just return the item as-is to satisfy resolve requests safely.
        return Task.FromResult(request);
    }

    public override async Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("CompletionHandler.Handle: Request received for {Uri} at position {Line}:{Character}", 
            request.TextDocument.Uri, request.Position.Line, request.Position.Character);

        var sw = Stopwatch.StartNew();
        CompletionList? result = null;
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);

        if (script is null)
        {
            _logger.LogWarning("CompletionHandler.Handle: Script is NULL for {Uri}", request.TextDocument.Uri);
            return new CompletionList();
        }

        _logger.LogInformation("CompletionHandler.Handle: Script found, calling GetCompletionAsync...");

        try
        {
            result = await script.GetCompletionAsync(request.Position, cancellationToken);
            _logger.LogInformation("CompletionHandler.Handle: GetCompletionAsync returned successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CompletionHandler.Handle: Exception in GetCompletionAsync");
            return new CompletionList();
        }

        sw.Stop();

        int count = result is null ? 0 : result.Count();
        _logger.LogInformation("CompletionHandler.Handle: Processed in {ElapsedMs} ms. Items: {Count}", sw.ElapsedMilliseconds, count);
        return result ?? new CompletionList();
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = _documentSelector,
            // Include additional triggers useful for namespaces, preprocessor, and paths
            TriggerCharacters = new List<string> { ".", ":", "#", "(", ",", "\\", "/" },
            ResolveProvider = true
        };
    }
}
