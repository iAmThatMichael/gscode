using GSCode.Parser;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Collections.Concurrent;
using System.IO;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    private readonly ScriptCache _cache;
    private readonly ILogger<ScriptManager> _logger;
    private readonly ILanguageServerFacade? _facade;

    private ConcurrentDictionary<DocumentUri, CachedScript> Scripts { get; } = new();

    /// <summary>
    /// Global symbol registry for workspace-wide symbol deduplication and O(1) lookup.
    /// </summary>
    private readonly GlobalSymbolRegistry _symbolRegistry = new();

    /// <summary>
    /// Provides read-only access to the global symbol registry for other components.
    /// </summary>
    public GlobalSymbolRegistry SymbolRegistry => _symbolRegistry;

    /// <summary>
    /// Optional user-configured custom path to the "raw" folder for path completions.
    /// This can be set via LSP configuration and overrides automatic detection.
    /// </summary>
    public string? CustomRawPath { get; set; }

    // Ensure only one parse per script at a time
    private readonly ConcurrentDictionary<DocumentUri, SemaphoreSlim> _parseLocks = new();
    // Ensure only one analysis/merge per script at a time
    private readonly ConcurrentDictionary<DocumentUri, SemaphoreSlim> _analysisLocks = new();

    // Editor priority gate: held during editor operations to pause the indexer dispatch loop
    private readonly SemaphoreSlim _editorPriority = new(1, 1);

    public ScriptManager(ILogger<ScriptManager> logger, ILanguageServerFacade? facade = null)
    {
        _cache = new();
        _logger = logger;
        _facade = facade;
    }

    private async Task EnsureParsedAsync(DocumentUri docUri, Script script, string? languageId, CancellationToken cancellationToken)
    {
        var gate = _parseLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!script.Parsed)
            {
                string path = docUri.ToUri().LocalPath;
                string content = await File.ReadAllTextAsync(path, cancellationToken);
                await script.ParseAsync(content);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WithAnalysisLockAsync(DocumentUri docUri, Func<Task> action, CancellationToken cancellationToken = default)
    {
        var gate = _analysisLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private void CleanupLocksForUri(DocumentUri uri)
    {
        if (_parseLocks.TryRemove(uri, out var parseLock))
            parseLock.Dispose();
        if (_analysisLocks.TryRemove(uri, out var analysisLock))
            analysisLock.Dispose();
    }

    private async Task PublishDiagnosticsAsync(DocumentUri uri, Script script, int? version = null, CancellationToken cancellationToken = default)
    {
        if (_facade is null) return;
        try
        {
            var diags = await script.GetDiagnosticsAsync(cancellationToken);
            _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = uri,
                Version = version,
                Diagnostics = new Container<Diagnostic>(diags)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish diagnostics for {Uri}", uri.GetFileSystemPath());
        }
    }
}
