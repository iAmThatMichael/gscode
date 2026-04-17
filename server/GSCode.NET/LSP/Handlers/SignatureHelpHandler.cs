using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.Diagnostics;

namespace GSCode.NET.LSP.Handlers;

internal class SignatureHelpHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : SignatureHelpHandlerBase
{
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    public override async Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        Log.Information("SignatureHelp request received, processing...");
        var sw = Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            sw.Stop();
            Log.Information("SignatureHelp finished in {ElapsedMs} ms: no script", sw.ElapsedMilliseconds);
            return null;
        }
        var help = await script.GetSignatureHelpAsync(request.Position, cancellationToken);
        sw.Stop();
        Log.Information("SignatureHelp finished in {ElapsedMs} ms. Has result: {Has}", sw.ElapsedMilliseconds, help != null);
        return help;
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _documentSelector,
            TriggerCharacters = new Container<string>("(", ",", ")"),
            RetriggerCharacters = new Container<string>(",", ")")
        };
}
