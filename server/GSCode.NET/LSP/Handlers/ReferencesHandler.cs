using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
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

        // Try local variable references first
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

        var results = new List<Location>();
        foreach (var loaded in scriptManager.GetLoadedScripts())
        {
            foreach (var key in keys)
            {
                if (loaded.Script.References.TryGetValue(key, out var ranges))
                    foreach (var r in ranges)
                        results.Add(new Location { Uri = loaded.Uri, Range = r });
            }

            if (request.Context?.IncludeDeclaration == true && loaded.Script.DefinitionsTable is not null)
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

        return new LocationContainer(results);
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(
        ReferenceCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
