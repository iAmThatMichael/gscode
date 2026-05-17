using Serilog;
using GSCode.Data;
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

    private ConcurrentDictionary<Uri, CachedScript> GscScripts { get; } = new(UriComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<Uri, CachedScript> CscScripts { get; } = new(UriComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Routes to the correct per-language script dictionary.
    /// All single-URI operations should use this instead of iterating AllScripts.
    /// </summary>
    private ConcurrentDictionary<Uri, CachedScript> GetScripts(ScriptLanguage language) =>
        language == ScriptLanguage.Csc ? CscScripts : GscScripts;

    /// <summary>
    /// Combined view of both language dictionaries for operations that genuinely need all scripts
    /// (cache serialisation, full cleanup, workspace-wide search anchoring).
    /// </summary>
    private IEnumerable<KeyValuePair<Uri, CachedScript>> AllScripts =>
        GscScripts.Concat(CscScripts);

    /// <summary>
    /// Per-language symbol registries. Each language gets its own isolated pool so
    /// that symbol lookups via ISymbolLocationProvider never cross language boundaries.
    /// </summary>
    private readonly ConcurrentDictionary<ScriptLanguage, GlobalSymbolRegistry> _symbolRegistries = new();

    /// <summary>
    /// Per-language field registries. Mirrors the symbol-registry split so that field completions
    /// (level.x, game.y, world.z) are also language-scoped.
    /// </summary>
    private readonly ConcurrentDictionary<ScriptLanguage, GlobalFieldRegistry> _fieldRegistries = new();

    /// <summary>
    /// Reverse map from an #insert file's absolute path to the set of editor/dependency
    /// URIs whose token stream includes that file.  Used to invalidate consumers when an
    /// insert file is saved or changed.
    /// Key: absolute insert-file path (OrdinalIgnoreCase).
    /// Value: concurrent set of dependent document URIs.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Uri, byte>> _insertDependents =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns (or creates) the symbol registry for <paramref name="language"/>.
    /// </summary>
    private GlobalSymbolRegistry GetSymbolRegistry(ScriptLanguage language) =>
        _symbolRegistries.GetOrAdd(language, _ => new GlobalSymbolRegistry());

    /// <summary>
    /// Returns (or creates) the field registry for <paramref name="language"/>.
    /// </summary>
    private GlobalFieldRegistry GetFieldRegistry(ScriptLanguage language) =>
        _fieldRegistries.GetOrAdd(language, _ => new GlobalFieldRegistry());

    /// <summary>
    /// Per-language symbol counts (used for diagnostics/telemetry only).
    /// </summary>
    public (int Functions, int Classes) GetSymbolCounts(ScriptLanguage language)
    {
        var reg = GetSymbolRegistry(language);
        return reg.GetCountsByType();
    }

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

    private async Task EnsureParsedAsync(Uri docUri, Script script, CancellationToken cancellationToken)
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

            foreach (var kvp in AllScripts)
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
    /// <summary>
    /// Builds a reverse-dependency map from the loaded cache:
    /// dep file path → set of file paths that depend on it.
    /// Used by the phase-2 stale-dependent reanalysis pass.
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildReverseDependencyMap(WorkspaceCacheFile cache)
    {
        var reverseMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, data) in cache.Scripts)
        {
            foreach (string dep in data.Dependencies)
            {
                if (!reverseMap.TryGetValue(dep, out var dependents))
                {
                    dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    reverseMap[dep] = dependents;
                }
                dependents.Add(filePath);
            }
        }
        return reverseMap;
    }

    private CachedScriptData? ExtractCacheData(string filePath, Script script, int contentHash)
    {
        var defTable = script.DefinitionsTable;
        if (defTable is null) return null;

        try
        {
            // Only persist locations that belong to this script itself.
            // Dependency-merged locations are rebuilt at load time via MergeDependencySymbolsAsync
            // and must not be cached, or they would bloat the per-script location dict run-over-run.
            var funcLocations = new Dictionary<Parser.SA.QualifiedSymbolKey, CachedSymbolLocation>();
            foreach (var kv in defTable.GetAllFunctionLocations())
            {
                if (string.Equals(kv.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    funcLocations[kv.Key] = new CachedSymbolLocation(kv.Value.FilePath, kv.Value.Range);
            }

            var classLocations = new Dictionary<Parser.SA.QualifiedSymbolKey, CachedSymbolLocation>();
            foreach (var kv in defTable.GetAllClassLocations())
            {
                if (string.Equals(kv.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    classLocations[kv.Key] = new CachedSymbolLocation(kv.Value.FilePath, kv.Value.Range);
            }

            // Extract function parameters, flags, and docs (scoped to own locations only)
            var funcParams = new Dictionary<Parser.SA.QualifiedSymbolKey, string[]>();
            var funcFlags = new Dictionary<Parser.SA.QualifiedSymbolKey, string[]>();
            var funcDocs = new Dictionary<Parser.SA.QualifiedSymbolKey, string?>();

            foreach (var kv in funcLocations)
            {
                var key = kv.Key;
                var parameters = defTable.GetFunctionParameters(key.Qualifier, key.SymbolName);
                if (parameters is not null) funcParams[key] = parameters;

                var flags = defTable.GetFunctionFlags(key.Qualifier, key.SymbolName);
                if (flags is not null) funcFlags[key] = flags;

                var doc = defTable.GetFunctionDoc(key.Qualifier, key.SymbolName);
                if (doc is not null) funcDocs[key] = doc;
            }

            // Snapshot diagnostics so they can be re-emitted on cache restore
            var cachedDiags = script.GetDiagnosticsSnapshot()
                .Select(d => new GSCode.Parser.Cache.CachedDiagnostic(
                    d.Range.Start.Line,
                    d.Range.Start.Character,
                    d.Range.End.Line,
                    d.Range.End.Character,
                    d.Severity,
                    d.Code?.IsString == true ? d.Code.Value.String : d.Code?.IsLong == true ? d.Code.Value.Long.ToString() : null,
                    d.Message,
                    d.Source
                ))
                .ToList();

            var macroPaths = script.GetMacroSourcePaths();

            // Build dep-hash map: dep file path → content hash at save time.
            // Only include deps whose CachedScript is currently loaded (hash is known).
            var depHashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Uri dep in script.UsingPaths)
            {
                string depPath = dep.LocalPath;
                ScriptLanguage depLang = ScriptLanguageExtensions.FromExtension(System.IO.Path.GetExtension(depPath));
                if (GetScripts(depLang).TryGetValue(dep, out var depCached))
                    depHashes[depPath] = depCached.LastContentHash;
            }

            // Build insert-dependencies list: resolved paths of all #insert files.
            // Hash each from disk so we can detect changes on the next workspace load.
            var insertDeps = new List<string>();
            foreach (string insertPath in script.InsertPaths)
            {
                insertDeps.Add(insertPath);
                if (!depHashes.ContainsKey(insertPath))
                {
                    try
                    {
                        string insertContent = File.ReadAllText(insertPath);
                        depHashes[insertPath] = WorkspaceCacheManager.GetDeterministicHashCode(insertContent);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to hash insert file {Path} for cache", insertPath);
                    }
                }
            }

            return new CachedScriptData
            {
                ContentHash = contentHash,
                LanguageId = script.Language.ToLanguageId(),
                CachedAt = DateTime.UtcNow,
                CurrentNamespace = defTable.CurrentNamespace,
                ExportedFunctions = defTable.ExportedFunctions.ToList(),
                ExportedClasses = defTable.ExportedClasses.ToList(),
                Dependencies = script.UsingPaths.Select(u => u.LocalPath).ToList(),
                FunctionLocations = funcLocations,
                ClassLocations = classLocations,
                FunctionParameters = funcParams,
                FunctionFlags = funcFlags,
                FunctionDocs = funcDocs,
                MacroDefinitions = macroPaths.ToDictionary(kv => kv.Key, kv => kv.Value),
                Diagnostics = cachedDiags,
                DependencyHashes = depHashes.Count > 0 ? depHashes : null,
                InsertDependencies = insertDeps.Count > 0 ? insertDeps : null
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to extract cache data for {File}", filePath);
            return null;
        }
    }
}


