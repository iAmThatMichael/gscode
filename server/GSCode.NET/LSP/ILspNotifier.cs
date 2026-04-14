using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.NET.LSP;

/// <summary>
/// Abstraction over the raw JSON-RPC channel for outbound LSP notifications.
/// Implemented by <see cref="GsCodeLanguageServer"/> and injected into
/// <see cref="ScriptManager"/> so the manager can push diagnostics without
/// depending directly on StreamJsonRpc.
/// </summary>
public interface ILspNotifier
{
    /// <summary>Sends a <c>textDocument/publishDiagnostics</c> notification to the client.</summary>
    Task PublishDiagnosticsAsync(Uri uri, IEnumerable<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default);
}
