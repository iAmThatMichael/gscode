using GSCode.Parser;
using GSCode.Parser.SA;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.Diagnostics;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

using SaSymbolKind = GSCode.Parser.SA.SymbolKind;

internal class DocumentHighlightHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : DocumentHighlightHandlerBase
{

    public override async Task<DocumentHighlightContainer?> Handle(DocumentHighlightParams request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null) return new DocumentHighlightContainer();

        Position position = request.Position;
        var localRefs = await GetLocalAsync(position);
        if (localRefs.Count == 0 && position.Character > 0)
        {
            localRefs = await GetLocalAsync(new Position(position.Line, position.Character - 1));
        }

        if (localRefs.Count > 0)
        {
            return new DocumentHighlightContainer(localRefs
                .Select(r => new DocumentHighlight { Range = r, Kind = DocumentHighlightKind.Read })
                .DistinctBy(h => RangeKey(h.Range)));
        }

        var qid = await script.GetQualifiedIdentifierAtAsync(position, cancellationToken);
        if (qid is null && position.Character > 0)
        {
            qid = await script.GetQualifiedIdentifierAtAsync(
                new Position(position.Line, position.Character - 1),
                cancellationToken);
        }

        if (qid is null)
            return new DocumentHighlightContainer();

        string ns = (qid.Value.qualifier ?? (script.DefinitionsTable?.CurrentNamespace ?? string.Empty)).ToLowerInvariant();
        string name = qid.Value.name.ToLowerInvariant();

        Range? declarationRange = script.DefinitionsTable?.GetFunctionLocation(ns, name)?.Range.ToRange()
            ?? script.DefinitionsTable?.GetClassLocation(ns, name)?.Range.ToRange()
            ?? script.DefinitionsTable?.GetFunctionLocationAnyNamespace(name)?.Range.ToRange()
            ?? script.DefinitionsTable?.GetClassLocationAnyNamespace(name)?.Range.ToRange();

        var highlights = new List<DocumentHighlight>();
        if (declarationRange is not null)
            highlights.Add(new DocumentHighlight { Range = declarationRange, Kind = DocumentHighlightKind.Write });

        var keys = new[]
        {
            new SymbolKey(SaSymbolKind.Function, ns, name),
            new SymbolKey(SaSymbolKind.Class, ns, name)
        };

        foreach (var key in keys)
        {
            if (!script.References.TryGetValue(key, out var ranges))
                continue;

            foreach (var range in ranges)
            {
                if (declarationRange is not null && RangeKey(range) == RangeKey(declarationRange))
                    continue;

                highlights.Add(new DocumentHighlight { Range = range, Kind = DocumentHighlightKind.Read });
            }
        }

        sw.Stop();
        Log.Information("DocumentHighlight finished in {ElapsedMs} ms. Highlights: {Count}",
            sw.ElapsedMilliseconds, highlights.Count);

        return new DocumentHighlightContainer(highlights.DistinctBy(h => RangeKey(h.Range)));

        async Task<IReadOnlyList<Range>> GetLocalAsync(Position pos)
            => await script.GetLocalVariableReferencesAsync(pos, includeDeclaration: true, cancellationToken);

        static (int, int, int, int) RangeKey(Range range)
            => (range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);
    }

    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(
        DocumentHighlightCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
