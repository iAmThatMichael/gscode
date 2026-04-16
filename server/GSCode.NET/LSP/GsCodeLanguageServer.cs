using Serilog;
using GSCode.Parser;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.IO;
using System.Linq;

namespace GSCode.NET.LSP;

/// <summary>
/// The GSCode language server.  Wires <see cref="StreamJsonRpc.JsonRpc"/> to
/// <see cref="ScriptManager"/> and implements every LSP method as a
/// <c>[JsonRpcMethod]</c> handler.  Replaces the former OmniSharp server builder.
/// </summary>
public sealed partial class GsCodeLanguageServer : ILspNotifier, IDisposable
{
    private readonly JsonRpc _rpc;
    private readonly ScriptManager _scriptManager;
    private readonly SemanticTokensLegend _semanticTokensLegend;

    /// <summary>
    /// Exposes the script manager for diagnostics and monitoring (e.g., memory debug stats).
    /// </summary>
    public ScriptManager ScriptManager => _scriptManager;

    // Workspace folders captured during Initialize for use in Initialized
    private string[] _workspaceFolders = [];
    private bool _shuttingDown;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public GsCodeLanguageServer(Stream input, Stream output)
    {
        // ScriptManager is injected with 'this' as the ILspNotifier so it can
        // push diagnostics back through the same JsonRpc channel.
        _scriptManager = new ScriptManager(this);

        _semanticTokensLegend = BuildLegend();

        var formatter = new JsonMessageFormatter();
        // The MS LSP SDK types use Newtonsoft.Json [JsonConverter] attributes on
        // individual properties for union types (SumType, etc.) and on enums that
        // need string serialisation.  Do NOT register a global StringEnumConverter
        // here — LSP enums like DiagnosticSeverity must be serialised as integers
        // per the LSP specification.

        HeaderDelimitedMessageHandler handler;
        if (ReferenceEquals(input, output))
        {
            // Single bidirectional stream (e.g. named pipe)
            handler = new HeaderDelimitedMessageHandler(input, formatter);
        }
        else
        {
            // Separate streams (e.g. stdio: output = send, input = receive)
            handler = new HeaderDelimitedMessageHandler(output, input, formatter);
        }

        _rpc = new JsonRpc(handler, this);
        _rpc.ExceptionStrategy = ExceptionProcessing.CommonErrorData;
        _rpc.Disconnected += OnDisconnected;

        // Enable verbose tracing to see incoming/outgoing messages
        _rpc.TraceSource = new System.Diagnostics.TraceSource("LSP", System.Diagnostics.SourceLevels.Verbose);
        _rpc.TraceSource.Listeners.Add(new SerilogTraceListener());
    }

    private void OnDisconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        Log.Warning("JsonRpc disconnected: {Reason} | LastMessage={LastMessage} | Exception={Exception}",
            e.Reason, e.LastMessage, e.Exception?.Message);
    }

    public void Start() => _rpc.StartListening();
    public Task WaitForExitAsync() => _rpc.Completion;

    public void Dispose() => _rpc.Dispose();

    // -------------------------------------------------------------------------
    // ILspNotifier
    // -------------------------------------------------------------------------

    public Task PublishDiagnosticsAsync(Uri uri, IEnumerable<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return _rpc.NotifyWithParameterObjectAsync(Methods.TextDocumentPublishDiagnosticsName,
                new PublishDiagnosticParams
                {
                    Uri = uri,
                    Diagnostics = diagnostics.ToArray()
                });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to publish diagnostics for {Uri}", uri.LocalPath);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Routes <see cref="System.Diagnostics.TraceSource"/> output to Serilog
/// so JsonRpc wire-level tracing appears in the same log stream.
/// </summary>
file sealed class SerilogTraceListener : System.Diagnostics.TraceListener
{
    public override void Write(string? message) => Serilog.Log.Verbose("[LSP] {Message}", message);
    public override void WriteLine(string? message) => Serilog.Log.Verbose("[LSP] {Message}", message);
}
// -------------------------------------------------------------------------
// (remainder moved to partial files — see GsCodeLanguageServer.*.cs)
// -------------------------------------------------------------------------
