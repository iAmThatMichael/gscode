using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.Diagnostics;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

using SymbolKindSA = GSCode.Parser.SA.SymbolKind;

internal class DefinitionHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : DefinitionHandlerBase
{

    public override async Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        string documentPath = request.TextDocument.Uri.ToUri().LocalPath;
        Log.Information("Definition request from {DocumentPath} at position {Position}", documentPath, request.Position);
        var sw = Stopwatch.StartNew();

        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            sw.Stop();
            Log.Information("Definition finished in {ElapsedMs} ms: no script for {DocumentPath}", sw.ElapsedMilliseconds, documentPath);
            return new LocationOrLocationLinks();
        }

        Location? location = await script.GetDefinitionAsync(request.Position, cancellationToken);
        if (location is not null)
        {
            sw.Stop();
            Log.Information("Definition resolved locally in {ElapsedMs} ms from {DocumentPath}: {Uri}:{Range}",
                sw.ElapsedMilliseconds, documentPath, location.Uri, location.Range);
            return new LocationOrLocationLinks(location);
        }

        // If the cursor is on a global dot-field (level.x, world.y, game.z), return the field token's own
        // location so VS Code navigates in-place rather than showing "No definition found".
        var fieldKey = await script.GetGlobalFieldAtAsync(request.Position, cancellationToken);
        if (fieldKey is not null)
        {
            sw.Stop();
            Log.Debug("Definition finished in {ElapsedMs} ms from {DocumentPath}: dot-field access [{Owner}, {Field}], returning self-location",
                sw.ElapsedMilliseconds, documentPath, fieldKey.Value.Owner, fieldKey.Value.Field);
            return new LocationOrLocationLinks(new Location
            {
                Uri = request.TextDocument.Uri.ToUri(),
                Range = fieldKey.Value.FieldRange
            });
        }

        // Try workspace-wide lookup
        var qual = await script.GetQualifiedIdentifierAtAsync(request.Position, cancellationToken);
        string? ns = qual?.qualifier;
        string name = qual?.name ?? "";

        if (string.IsNullOrEmpty(name))
        {
            sw.Stop();
            return new LocationOrLocationLinks();
        }

        // Skip built-in API functions
        var api = ScriptAnalyserData.GetShared(script.LanguageId);
        if (api is not null)
        {
            var apiFn = api.GetApiFunction(name);
            if (apiFn is not null && apiFn.IsBuiltIn)
            {
                sw.Stop();
                return new LocationOrLocationLinks();
            }
        }

        Location? remote = scriptManager.FindSymbolLocation(ns, name, documentPath);
        sw.Stop();
        if (remote is not null)
        {
            Log.Information("Definition resolved remotely in {ElapsedMs} ms from {DocumentPath}: {Uri}:{Range}",
                sw.ElapsedMilliseconds, documentPath, remote.Uri, remote.Range);
            return new LocationOrLocationLinks(remote);
        }

        Log.Information("Definition finished in {ElapsedMs} ms from {DocumentPath}: not found for {Name}",
            sw.ElapsedMilliseconds, documentPath, name);
        return new LocationOrLocationLinks();
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
