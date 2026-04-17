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
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    public override async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        Log.Information("Hover request received, processing...");
        var sw = Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null) { sw.Stop(); return null; }
        var result = await script.GetHoverAsync(request.Position, cancellationToken);
        sw.Stop();
        Log.Information("Hover processed in {ElapsedMs} ms. Has result: {Has}", sw.ElapsedMilliseconds, result != null);
        return result;
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = _documentSelector };
}
