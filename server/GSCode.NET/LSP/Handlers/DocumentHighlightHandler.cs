using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

internal class DocumentHighlightHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : DocumentHighlightHandlerBase
{

    public override async Task<DocumentHighlightContainer?> Handle(DocumentHighlightParams request, CancellationToken cancellationToken)
    {
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null) return new DocumentHighlightContainer();

        var ranges = await script.GetLocalVariableReferencesAsync(request.Position, includeDeclaration: true, cancellationToken);
        return new DocumentHighlightContainer(
            ranges.Select(r => new DocumentHighlight { Range = r, Kind = DocumentHighlightKind.Text }));
    }

    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(
        DocumentHighlightCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
