using Serilog;
using GSCode.Parser;
using GSCode.Parser.Cache;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Concurrent;
using System.IO;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    private readonly ScriptCache _cache;
    private readonly ILspNotifier? _notifier;

    /// <summary>
    /// In-memory workspace cache loaded from disk on startup.
    /// Null until the first indexing pass loads (or fails to load) the cache file.
    /// </summary>
    private WorkspaceCacheFile? _workspaceCache;

    private ConcurrentDictionary<Uri, CachedScript> Scripts { get; } = new(UriComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Global symbol registry for workspace-wide symbol deduplication and O(1) lookup.
    /// </summary>
    private readonly GlobalSymbolRegistry _symbolRegistry = new();

    /// <summary>
    /// Provides read-only access to the global symbol registry for other components.
    /// </summary>
    public GlobalSymbolRegistry SymbolRegistry => _symbolRegistry;

    /// <summary>
    /// Global field registry for cross-file tracking of fields on global objects (level, world, game).
    /// </summary>
    private readonly GlobalFieldRegistry _fieldRegistry = new();

    /// <summary>
    /// Provides read-only access to the global field registry for other components.
    /// </summary>
    public GlobalFieldRegistry FieldRegistry => _fieldRegistry;

    /// <summary>
    /// Optional user-configured custom path to the "raw" folder for path completions.
    /// This can be set via LSP configuration and overrides automatic detection.
    /// </summary>
    public string? CustomRawPath { get; set; }

    /// <summary>
    /// Controls whether the persistent workspace parse cache is used.
    /// When false, cache loading, cache hits, and cache saving are all skipped.
    /// </summary>
    public bool UseWorkspaceCache { get; set; } = true;

    // Ensure only one parse per script at a time
    private readonly ConcurrentDictionary<Uri, SemaphoreSlim> _parseLocks = new(UriComparer.OrdinalIgnoreCase);
    // Ensure only one analysis/merge per script at a time
    private readonly ConcurrentDictionary<Uri, SemaphoreSlim> _analysisLocks = new(UriComparer.OrdinalIgnoreCase);

    // Editor priority gate: held during editor operations to pause the indexer dispatch loop
    private readonly SemaphoreSlim _editorPriority = new(1, 1);

    /// <summary>
    /// Set to true once IndexWorkspaceAsync has fully completed (all files parsed and
    /// all macros inserted into MacroDefinitionCache). Consumers that want a stable
    /// snapshot of macro/GSH counts should wait for this flag before reading.
    /// </summary>
    public volatile bool IsIndexingComplete = false;

    public ScriptManager(ILspNotifier? notifier = null)
    {
        _cache = new();
        _notifier = notifier;
    }

    private async Task EnsureParsedAsync(Uri docUri, Script script, string? languageId, CancellationToken cancellationToken)
    {
        var gate = _parseLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!script.Parsed)
            {
                string path = UriHelper.GetLocalPath(docUri);
                string content = await File.ReadAllTextAsync(path, cancellationToken);
                await script.ParseAsync(content);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WithAnalysisLockAsync(Uri docUri, Func<Task> action, CancellationToken cancellationToken = default)
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

    private void CleanupLocksForUri(Uri uri)
    {
        if (_parseLocks.TryRemove(uri, out var parseLock))
            parseLock.Dispose();
        if (_analysisLocks.TryRemove(uri, out var analysisLock))
            analysisLock.Dispose();
    }

    private async Task PublishDiagnosticsAsync(Uri uri, Script script, CancellationToken cancellationToken = default)
    {
        if (_notifier is null) return;
        try
        {
            var diags = await script.GetDiagnosticsAsync(cancellationToken);
            await _notifier.PublishDiagnosticsAsync(uri, diags, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to publish diagnostics for {Uri}", UriHelper.GetLocalPath(uri));
        }
    }

    /// <summary>
    /// Saves the current workspace state to the persistent cache file.
    /// Safe to call at any time — serializes a snapshot of all parsed scripts.
    /// </summary>
    public async Task SaveWorkspaceCacheAsync()
    {
        try
        {
            string cacheFilePath = WorkspaceCacheManager.GetCacheFilePath();
            var scripts = new Dictionary<string, CachedScriptData>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in Scripts)
            {
                string filePath = UriHelper.GetLocalPath(kvp.Key);
                var cached = kvp.Value;
                var script = cached.Script;

                if (!script.Parsed || script.Failed || script.DefinitionsTable is null)
                    continue;

                var data = ExtractCacheData(filePath, script, cached.LastContentHash);
                if (data is not null)
                {
                    scripts[filePath] = data;
                }
            }

            var cacheFile = new WorkspaceCacheFile
            {
                FormatVersion = WorkspaceCacheManager.CacheFormatVersion,
                ServerVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                LastSaved = DateTime.UtcNow,
                Scripts = scripts
            };

            await WorkspaceCacheManager.SaveAsync(cacheFilePath, cacheFile);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save workspace cache");
        }
    }

    /// <summary>
    /// Extracts a CachedScriptData DTO from a parsed script for serialization.
    /// </summary>
    private static CachedScriptData? ExtractCacheData(string filePath, Script script, int contentHash)
    {
        var defTable = script.DefinitionsTable;
        if (defTable is null) return null;

        try
        {
            var funcLocations = new Dictionary<Parser.SA.QualifiedSymbolKey, CachedSymbolLocation>();
            foreach (var kv in defTable.GetAllFunctionLocations())
            {
                funcLocations[kv.Key] = new CachedSymbolLocation(kv.Value.FilePath, kv.Value.Range);
            }

            var classLocations = new Dictionary<Parser.SA.QualifiedSymbolKey, CachedSymbolLocation>();
            foreach (var kv in defTable.GetAllClassLocations())
            {
                classLocations[kv.Key] = new CachedSymbolLocation(kv.Value.FilePath, kv.Value.Range);
            }

            // Extract function parameters, flags, and docs
            var funcParams = new Dictionary<Parser.SA.QualifiedSymbolKey, string[]>();
            var funcFlags = new Dictionary<Parser.SA.QualifiedSymbolKey, string[]>();
            var funcDocs = new Dictionary<Parser.SA.QualifiedSymbolKey, string?>();

            foreach (var kv in defTable.GetAllFunctionLocations())
            {
                var key = kv.Key;
                var parameters = defTable.GetFunctionParameters(key.Qualifier, key.SymbolName);
                if (parameters is not null) funcParams[key] = parameters;

                var flags = defTable.GetFunctionFlags(key.Qualifier, key.SymbolName);
                if (flags is not null) funcFlags[key] = flags;

                var doc = defTable.GetFunctionDoc(key.Qualifier, key.SymbolName);
                if (doc is not null) funcDocs[key] = doc;
            }

            return new CachedScriptData
            {
                ContentHash = contentHash,
                LanguageId = script.LanguageId,
                CachedAt = DateTime.UtcNow,
                CurrentNamespace = defTable.CurrentNamespace,
                ExportedFunctions = defTable.ExportedFunctions.ToList(),
                ExportedClasses = defTable.ExportedClasses.ToList(),
                Dependencies = defTable.Dependencies.Select(u => u.LocalPath).ToList(),
                FunctionLocations = funcLocations,
                ClassLocations = classLocations,
                FunctionParameters = funcParams,
                FunctionFlags = funcFlags,
                FunctionDocs = funcDocs,
                MacroDefinitions = new Dictionary<string, string>()
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to extract cache data for {File}", filePath);
            return null;
        }
    }
}


