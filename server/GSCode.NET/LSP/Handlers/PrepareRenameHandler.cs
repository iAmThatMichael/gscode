using GSCode.Data;
using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace GSCode.NET.LSP.Handlers;

internal class PrepareRenameHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : PrepareRenameHandlerBase
{
    public override async Task<RangeOrPlaceholderRange?> Handle(PrepareRenameParams request, CancellationToken cancellationToken)
    {
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null) return null;

        var target = await script.GetRenameTargetAtAsync(request.Position, cancellationToken);
        if (target is null) return null;

        string name = target.Value.Name;

        // Reject built-in API functions — they are not user-defined and cannot be renamed.
        var api = ScriptAnalyserData.GetShared(script.Language);
        if (api is not null)
        {
            var apiFn = api.GetApiFunction(name);
            if (apiFn is not null && apiFn.IsBuiltIn) return null;
        }

        return new RangeOrPlaceholderRange(new PlaceholderRange
        {
            Range = target.Value.Range,
            Placeholder = name
        });
    }

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = documentSelector,
            PrepareProvider = true
        };
}
