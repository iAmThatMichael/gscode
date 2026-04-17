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
}
