using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.Diagnostics;

namespace GSCode.NET.LSP.Handlers;

internal class HoverHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : HoverHandlerBase
{

    public override async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        Log.Information("Hover request received, processing...");
        var sw = Stopwatch.StartNew();
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null) { sw.Stop(); return null; }
        var result = await script.GetHoverAsync(request.Position, cancellationToken);
        sw.Stop();
        Log.Information("Hover processed in {ElapsedMs} ms. Has result: {Has}", sw.ElapsedMilliseconds, result != null);
        return result;
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = documentSelector };
}
