using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.SPA;

namespace GSCode.NET.LSP.Handlers;

internal class DefinitionHandler : DefinitionHandlerBase
{
    private readonly ScriptManager _scriptManager;
    private readonly ILogger<DefinitionHandler> _logger;
    private readonly TextDocumentSelector _document_selector;

    public DefinitionHandler(ScriptManager scriptManager,
        ILogger<DefinitionHandler> logger,
        TextDocumentSelector documentSelector)
    {
        _scriptManager = scriptManager;
        _logger = logger;
        _document_selector = documentSelector;
    }

    public override async Task<LocationOrLocationLinks> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        string documentUri = request.TextDocument.Uri.ToString();
        string documentPath = request.TextDocument.Uri.ToUri().LocalPath;
        _logger.LogInformation("Definition request received from document: {DocumentPath} at position {Position}", documentPath, request.Position);
        var sw = Stopwatch.StartNew();

        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            sw.Stop();
            _logger.LogInformation("Definition finished in {ElapsedMs} ms: no script for {DocumentPath}", sw.ElapsedMilliseconds, documentPath);
            return new LocationOrLocationLinks();
        }

        // Try local script lookup first
        Location? location = await script.GetDefinitionAsync(request.Position, cancellationToken);
        if (location is not null)
        {
            sw.Stop();
            _logger.LogInformation("Definition resolved locally in {ElapsedMs} ms from {SourceDocument}: {uri}:{range}", 
                sw.ElapsedMilliseconds, documentPath, location.Uri, location.Range);
            return new LocationOrLocationLinks(location);
        }

        // If not found locally, get the qualified identifier using published API
        var qual = await script.GetQualifiedIdentifierAtAsync(request.Position, cancellationToken);
        string? ns = qual?.qualifier;
        string name = qual?.name ?? "";

        if (string.IsNullOrEmpty(name))
        {
            sw.Stop();
            _logger.LogInformation("Definition finished in {ElapsedMs} ms from {SourceDocument}: no identifier", 
                sw.ElapsedMilliseconds, documentPath);
            return new LocationOrLocationLinks();
        }

        // If it's a built-in API function, do not return a file location
        var api = ScriptAnalyserData.GetShared(script.LanguageId);
        if (api is not null)
        {
            var apiFn = api.GetApiFunction(name);
            if (apiFn is not null && apiFn.IsBuiltIn)
            {
                sw.Stop();
                _logger.LogInformation("Definition finished in {ElapsedMs} ms from {SourceDocument}: built-in API {name}", 
                    sw.ElapsedMilliseconds, documentPath, name);
                return new LocationOrLocationLinks();
            }
        }

        // Get current file path for workspace boundary filtering
        string? currentFilePath = documentPath;
        Location? remote = _scriptManager.FindSymbolLocation(ns, name, currentFilePath);
        sw.Stop();
        if (remote is not null)
        {
            _logger.LogInformation("Definition resolved remotely in {ElapsedMs} ms from {SourceDocument}: {uri}:{range}", 
                sw.ElapsedMilliseconds, documentPath, remote.Uri, remote.Range);
            return new LocationOrLocationLinks(remote);
        }

        _logger.LogInformation("Definition finished in {ElapsedMs} ms from {SourceDocument}: not found for {name}", 
            sw.ElapsedMilliseconds, documentPath, name);
        return new LocationOrLocationLinks();
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions()
        {
            DocumentSelector = _document_selector
        };
    }
}
