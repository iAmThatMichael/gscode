using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.IO;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

internal class ReferencesHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : ReferencesHandlerBase
{

    public override async Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken cancellationToken)
    {
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null) return new LocationContainer();

        // Try dot-field references first — must run before local variable check so dot-preceded
        // identifiers aren't incorrectly matched as locals.
        var fieldAccess = await script.GetGlobalFieldAtAsync(request.Position, cancellationToken);
        if (fieldAccess is not null)
        {
            var fieldKey = new SymbolKey(GSCode.Parser.SA.SymbolKind.Field, "", fieldAccess.Value.Field);
            var fieldResults = new List<Location>();
            foreach (var loaded in scriptManager.GetLoadedScripts())
            {
                if (!string.Equals(loaded.Script.LanguageId, script.LanguageId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (loaded.Script.References.TryGetValue(fieldKey, out var ranges))
                    foreach (var r in ranges)
                        fieldResults.Add(new Location { Uri = loaded.Uri, Range = r });
            }
            return new LocationContainer(fieldResults);
        }

        // Try local variable references
        var localRefs = await script.GetLocalVariableReferencesAsync(
            request.Position, request.Context?.IncludeDeclaration == true, cancellationToken);
        if (localRefs.Count > 0)
            return new LocationContainer(localRefs.Select(r => new Location { Uri = request.TextDocument.Uri.ToUri(), Range = r }));

        var qid = await script.GetQualifiedIdentifierAtAsync(request.Position, cancellationToken);
        if (qid is null) return new LocationContainer();

        string ns = qid.Value.qualifier ?? (script.DefinitionsTable?.CurrentNamespace ?? "");
        string name = qid.Value.name;

        var keys = new[]
        {
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Function, ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Method,   ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Class,    ns, name)
        };

        string requestingLanguageId = script.LanguageId;

        var results = new List<Location>();
        foreach (var loaded in scriptManager.GetLoadedScripts())
        {
            // Don't cross GSC/CSC boundaries — a reference in a .gsc file cannot appear in a .csc file and vice versa.
            if (!string.Equals(loaded.Script.LanguageId, requestingLanguageId, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var key in keys)
            {
                if (loaded.Script.References.TryGetValue(key, out var ranges))
                {
                    foreach (var r in ranges)
                    {
                        results.Add(new Location { Uri = loaded.Uri, Range = r });
                    }
                }
            }

            if (request.Context?.IncludeDeclaration == true && loaded.Script.DefinitionsTable is not null)
            {
                foreach (var key in keys)
                {
                    var loc = loaded.Script.DefinitionsTable.GetSymbolLocation(key.Namespace, key.Name);

                    if (loc is not null)
                    {
                        // loc.Value.FilePath may be a bare game-relative path (e.g. "scripts\shared\array_shared.gsc")
                        // when it was merged from a dependency. Resolve it the same way FindSymbolLocation does —
                        // using the host script's absolute path as the base so the game root is known.
                        string? resolved = ScriptFileResolver.GetScriptFilePath(loaded.Uri.LocalPath, loc.Value.FilePath);
                        if (resolved is null || !File.Exists(resolved)) continue;
                        results.Add(new Location { Uri = new Uri(resolved), Range = loc.Value.Range.ToRange() });
                    }
                }
            }
        }

        return new LocationContainer(results);
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(
        ReferenceCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
