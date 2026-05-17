using GSCode.Data.Models.Interfaces;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace GSCode.NET.LSP.Handlers;

internal class WorkspaceSymbolHandler(ScriptManager scriptManager) : WorkspaceSymbolsHandlerBase
{
    private const int MaxResults = 250;

    public override Task<Container<WorkspaceSymbol>?> Handle(
        WorkspaceSymbolParams request, CancellationToken cancellationToken)
    {
        var results = new List<WorkspaceSymbol>();

        foreach (var (symbol, uri, range) in scriptManager.SearchWorkspaceSymbols(request.Query, MaxResults))
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(new WorkspaceSymbol
            {
                Name = symbol.Name,
                Kind = symbol.Type == ExportedSymbolType.Function ? SymbolKind.Function : SymbolKind.Class,
                ContainerName = string.IsNullOrEmpty(symbol.Namespace) ? null : symbol.Namespace,
                Location = new Location { Uri = uri, Range = range }
            });
        }

        return Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(results));
    }

    protected override WorkspaceSymbolRegistrationOptions CreateRegistrationOptions(
        WorkspaceSymbolCapability capability, ClientCapabilities clientCapabilities)
        => new();
}
