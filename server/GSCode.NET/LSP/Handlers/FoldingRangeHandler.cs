using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.Diagnostics;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

internal class FoldingRangeHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : FoldingRangeHandlerBase
{

    public override async Task<Container<FoldingRange>?> Handle(FoldingRangeRequestParam request, CancellationToken cancellationToken)
    {
        Log.Information("Folding range request received, processing...");
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null) return new Container<FoldingRange>();
        var ranges = await script.GetFoldingRangesAsync(cancellationToken);
        var result = ranges.ToArray();
        Log.Information("Folding range request processed. FoldingRanges: {Count}", result.Length);
        return new Container<FoldingRange>(result);
    }

    protected override FoldingRangeRegistrationOptions CreateRegistrationOptions(
        FoldingRangeCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
