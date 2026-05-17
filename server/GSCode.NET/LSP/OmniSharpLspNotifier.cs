using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace GSCode.NET.LSP;

/// <summary>
/// Adapts <see cref="ILanguageServerFacade"/> to <see cref="ILspNotifier"/>
/// so that <see cref="ScriptManager"/> can push diagnostics without depending
/// directly on OmniSharp.
/// </summary>
public sealed class OmniSharpLspNotifier(ILanguageServerFacade facade) : ILspNotifier
{
    public Task PublishDiagnosticsAsync(Uri uri, IEnumerable<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>(diagnostics)
        });
        return Task.CompletedTask;
    }

    public Task SendIndexingStartedAsync(int totalFiles, CancellationToken cancellationToken = default)
    {
        facade.SendNotification("gscode/indexingStarted", new { totalFiles });
        return Task.CompletedTask;
    }

    public Task SendIndexingProgressAsync(int filesIndexed, int totalFiles, CancellationToken cancellationToken = default)
    {
        facade.SendNotification("gscode/indexingProgress", new { filesIndexed, totalFiles });
        return Task.CompletedTask;
    }

    public Task SendIndexingCompleteAsync(int filesIndexed, int totalFiles, int fromCache, CancellationToken cancellationToken = default)
    {
        facade.SendNotification("gscode/indexingComplete", new { filesIndexed, totalFiles, fromCache });
        return Task.CompletedTask;
    }
}
