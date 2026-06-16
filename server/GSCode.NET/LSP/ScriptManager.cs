using Serilog;
using GSCode.Data;
using GSCode.Parser;
using GSCode.Parser.Cache;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

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

    private bool _workspaceCacheDirty;

    private sealed record FileSnapshot(string Path, string? Content, int ContentHash, bool Exists);

    private sealed class FileSnapshotCache
    {
        private readonly ConcurrentDictionary<string, Task<FileSnapshot>> _snapshots = new(StringComparer.OrdinalIgnoreCase);

        public async Task<FileSnapshot> GetAsync(string filePath, CancellationToken cancellationToken = default)
        {
            string normalizedPath = NormalizeCachePath(filePath);
            cancellationToken.ThrowIfCancellationRequested();

            Task<FileSnapshot> snapshotTask = _snapshots.GetOrAdd(normalizedPath, ReadSnapshotAsync);

            FileSnapshot snapshot = await snapshotTask;
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }

        private static async Task<FileSnapshot> ReadSnapshotAsync(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new FileSnapshot(path, null, 0, Exists: false);

                string content = await File.ReadAllTextAsync(path);
                int hash = WorkspaceCacheManager.GetDeterministicHashCode(content);
                return new FileSnapshot(path, content, hash, Exists: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read file snapshot for {Path}", path);
                return new FileSnapshot(path, null, 0, Exists: false);
            }
        }
    }

    private sealed class IndexingContext(WorkspaceCacheFile? workspaceCache)
    {
        public WorkspaceCacheFile? WorkspaceCache { get; } = workspaceCache;
        public FileSnapshotCache FileSnapshots { get; } = new();
    }

    private static readonly Lazy<string> ServerBuildIdentity = new(CreateServerBuildIdentity);

    private static string CurrentServerVersion => ServerBuildIdentity.Value;

    private static string CreateServerBuildIdentity()
    {
        var parts = new List<string>
        {
            GetAssemblyBuildId(typeof(ScriptManager).Assembly),
            GetAssemblyBuildId(typeof(Script).Assembly),
            GetAssemblyBuildId(typeof(GsPosition).Assembly)
        };

        string? assemblyDirectory = Path.GetDirectoryName(typeof(ScriptManager).Assembly.Location);
        if (assemblyDirectory is not null)
        {
            string apiDirectory = Path.Combine(assemblyDirectory, "api");
            parts.Add(GetFileBuildId(Path.Combine(apiDirectory, "t7_api_gsc.json")));
            parts.Add(GetFileBuildId(Path.Combine(apiDirectory, "t7_api_csc.json")));
        }
        else
        {
            parts.Add("api:unknown");
        }

        return string.Join("|", parts);
    }

    private static string GetAssemblyBuildId(Assembly assembly)
    {
        AssemblyName name = assembly.GetName();
        return $"{name.Name}:{name.Version}:{assembly.ManifestModule.ModuleVersionId:N}";
    }

    private static string GetFileBuildId(string path)
    {
        string fileName = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            return $"{fileName}:missing";
        }

        byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
        return $"{fileName}:{Convert.ToHexString(hash)}";
    }

    private static string NormalizeCachePath(string path)
    {
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..];

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static bool TryComputeContentHash(string filePath, out int hash)
    {
        hash = 0;
        try
        {
            if (!File.Exists(filePath))
                return false;

            string content = File.ReadAllText(filePath);
            hash = WorkspaceCacheManager.GetDeterministicHashCode(content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Dictionary<string, int>> BuildDependencyContentHashesAsync(
        IEnumerable<string> dependencies,
        FileSnapshotCache? snapshotCache,
        CancellationToken cancellationToken = default)
    {
        var hashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string dependency in dependencies)
        {
            string path = NormalizeCachePath(dependency);
            if (snapshotCache is not null)
            {
                FileSnapshot snapshot = await snapshotCache.GetAsync(path, cancellationToken);
                if (snapshot.Exists)
                    hashes[snapshot.Path] = snapshot.ContentHash;
            }
            else if (TryComputeContentHash(path, out int hash))
            {
                hashes[path] = hash;
            }
        }

        return hashes;
    }

    private static async Task<(bool IsCurrent, string? ChangedDependency)> DependencyContentHashesAreCurrentAsync(
        CachedScriptData cachedData,
        FileSnapshotCache? snapshotCache,
        CancellationToken cancellationToken = default)
    {
        foreach (var (cachedPath, cachedHash) in cachedData.DependencyContentHashes)
        {
            string path = NormalizeCachePath(cachedPath);
            int currentHash;
            bool exists;

            if (snapshotCache is not null)
            {
                FileSnapshot snapshot = await snapshotCache.GetAsync(path, cancellationToken);
                exists = snapshot.Exists;
                currentHash = snapshot.ContentHash;
            }
            else
            {
                exists = TryComputeContentHash(path, out currentHash);
            }

            if (!exists || cachedHash != currentHash)
                return (false, path);
        }

        foreach (string dependency in cachedData.Dependencies)
        {
            string path = NormalizeCachePath(dependency);
            if (!cachedData.DependencyContentHashes.ContainsKey(path))
                return (false, path);
        }

        return (true, null);
    }

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

    private async Task EnsureParsedAsync(Uri docUri, Script script, CancellationToken cancellationToken, string? knownContent = null)
    {
        var gate = _parseLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!script.Parsed)
            {
                string path = UriHelper.GetLocalPath(docUri);
                string content = knownContent ?? await File.ReadAllTextAsync(path, cancellationToken);
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
        // SemaphoreSlim has no safe "dispose when nobody is about to wait" primitive.
        // These locks are tiny and keyed by URI, so keep them for the manager lifetime
        // instead of risking disposal while editor and indexing work overlap.
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
        => await SaveWorkspaceCacheAsync(snapshotCache: null, CancellationToken.None);

    private async Task SaveWorkspaceCacheAsync(FileSnapshotCache? snapshotCache, CancellationToken cancellationToken)
    {
        try
        {
            if (!_workspaceCacheDirty)
            {
                Log.Debug("Workspace cache save skipped: no dirty scripts");
                return;
            }

            string cacheFilePath = WorkspaceCacheManager.GetCacheFilePath();
            WorkspaceCacheFile? existingCache = _workspaceCache;
            if (existingCache is null && File.Exists(cacheFilePath))
            {
                existingCache = await WorkspaceCacheManager.LoadAsync(cacheFilePath, CurrentServerVersion);
            }

            var scripts = existingCache?.Scripts
                .Where(kv => File.Exists(NormalizeCachePath(kv.Key)))
                .ToDictionary(kv => NormalizeCachePath(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, CachedScriptData>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in AllScripts)
            {
                string filePath = NormalizeCachePath(UriHelper.GetLocalPath(kvp.Key));
                var cached = kvp.Value;
                var script = cached.Script;

                if (!script.Parsed || script.Failed || script.DefinitionsTable is null)
                    continue;

                if (!cached.WorkspaceCacheDirty && scripts.ContainsKey(filePath))
                    continue;

                var data = await ExtractCacheDataAsync(filePath, script, cached.LastContentHash, snapshotCache, cancellationToken);
                if (data is not null)
                {
                    scripts[filePath] = data;
                    cached.WorkspaceCacheDirty = false;
                }
            }

            var cacheFile = new WorkspaceCacheFile
            {
                FormatVersion = WorkspaceCacheManager.CacheFormatVersion,
                ServerVersion = CurrentServerVersion,
                LastSaved = DateTime.UtcNow,
                Scripts = scripts
            };

            await WorkspaceCacheManager.SaveAsync(cacheFilePath, cacheFile);
            _workspaceCache = cacheFile;
            _workspaceCacheDirty = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save workspace cache");
        }
    }

    /// <summary>
    /// Extracts a CachedScriptData DTO from a parsed script for serialization.
    /// </summary>
    private static async Task<CachedScriptData?> ExtractCacheDataAsync(
        string filePath,
        Script script,
        int contentHash,
        FileSnapshotCache? snapshotCache,
        CancellationToken cancellationToken = default)
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

            var dependencies = defTable.Dependencies
                .Select(u => NormalizeCachePath(u.LocalPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var macroPaths = script.GetMacroSourcePaths();

            var cachedReferences = script.References
                .SelectMany(kv => kv.Value.Select(range => new CachedReference(
                    kv.Key.Kind,
                    kv.Key.Namespace,
                    kv.Key.Name,
                    kv.Key.ClassName,
                    kv.Key.ScopeId,
                    range.Start.Line,
                    range.Start.Character,
                    range.End.Line,
                    range.End.Character)))
                .ToList();

            var cachedGlobalFields = script.ExtractGlobalFieldAccesses()
                .Select(field => new CachedGlobalFieldAccess(field.OwnerName, field.FieldName))
                .ToList();

            return new CachedScriptData
            {
                ContentHash = contentHash,
                LanguageId = script.Language.ToLanguageId(),
                CachedAt = DateTime.UtcNow,
                CurrentNamespace = defTable.CurrentNamespace,
                ExportedFunctions = defTable.ExportedFunctions.ToList(),
                ExportedClasses = defTable.ExportedClasses.ToList(),
                Dependencies = dependencies,
                DependencyContentHashes = await BuildDependencyContentHashesAsync(dependencies.Concat(
                    macroPaths.Values
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => path!)),
                    snapshotCache,
                    cancellationToken),
                FunctionLocations = funcLocations,
                ClassLocations = classLocations,
                FunctionParameters = funcParams,
                FunctionFlags = funcFlags,
                FunctionDocs = funcDocs,
                MacroDefinitions = macroPaths.ToDictionary(kv => kv.Key, kv => kv.Value),
                Diagnostics = cachedDiags,
                References = cachedReferences,
                GlobalFieldAccesses = cachedGlobalFields
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to extract cache data for {File}", filePath);
            return null;
        }
    }
}


