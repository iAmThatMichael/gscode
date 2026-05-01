using Serilog;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Cache;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Diagnostics;
using System.IO;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    public async Task IndexWorkspaceAsync(string rootDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            // Normalise to backslashes so Windows IO APIs are happy
            rootDirectory = Path.GetFullPath(rootDirectory);

            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                Log.Warning("IndexWorkspace skipped: directory not found: {Root}", rootDirectory);
                return;
            }

            // Load persistent cache from disk
            string cacheFilePath = WorkspaceCacheManager.GetCacheFilePath();
            _workspaceCache = UseWorkspaceCache
                ? await WorkspaceCacheManager.LoadAsync(cacheFilePath)
                : null;

            var filesList = Directory
                .EnumerateFiles(rootDirectory, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".csc", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Log.Information("Indexing workspace under {Root}", rootDirectory);
            Log.Information("Indexing started: {Count} files", filesList.Count);
            var swAll = Stopwatch.StartNew();

            int maxDegree = Math.Max(1, Environment.ProcessorCount - 1);
            using SemaphoreSlim gate = new(maxDegree, maxDegree);
            List<Task> tasks = new();
            int cacheHits = 0;

            int fileIndex = 0;
            foreach (string file in filesList)
            {
                // Yield to editor operations every 10 files to reduce semaphore overhead
                if (fileIndex++ % 10 == 0)
                {
                    await _editorPriority.WaitAsync(cancellationToken);
                    _editorPriority.Release();
                }

                await gate.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(async () =>
                {
#if DEBUG
                    var fileSw = Stopwatch.StartNew();
#endif
                    string rel = Path.GetRelativePath(rootDirectory, file);
                    try
                    {
#if DEBUG
                        Log.Information("Indexing {File}", rel);
#endif
                        bool fromCache = await IndexFileAsync(file, cancellationToken);
                        if (fromCache) Interlocked.Increment(ref cacheHits);
#if DEBUG
                        fileSw.Stop();
                        Log.Information("{Status} {File} in {ElapsedMs} ms",
                            fromCache ? "Restored" : "Indexed", rel, fileSw.ElapsedMilliseconds);
#endif
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to index {File}", file);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);

            swAll.Stop();
            Log.Information("Indexing completed in {ElapsedMs} ms for {Count} files ({CacheHits} from cache, {Parsed} parsed)",
                swAll.ElapsedMilliseconds, filesList.Count, cacheHits, filesList.Count - cacheHits);

            // Signal that all files (and their #insert'd GSH macros) are now in the cache.
            // The memory monitor reads this before logging stable GSH/Macro counts.
            IsIndexingComplete = true;

            // Save updated cache to disk
            if (UseWorkspaceCache)
            {
                await SaveWorkspaceCacheAsync();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Indexing cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Indexing failed for {Root}", rootDirectory);
        }
    }

    /// <summary>
    /// Indexes a single file. Returns true if the file was restored from cache, false if parsed from scratch.
    /// </summary>
    private async Task<bool> IndexFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string ext = Path.GetExtension(filePath);
        string languageId = string.Equals(ext, ".csc", StringComparison.OrdinalIgnoreCase) ? "csc" : "gsc";

        Uri docUri = new Uri(filePath);

        // Try to restore from persistent cache before parsing
        bool restoredFromCache = false;
        if (_workspaceCache is not null &&
            _workspaceCache.Scripts.TryGetValue(filePath, out var cachedData))
        {
            try
            {
                // Read file content and compute hash
                string content = await File.ReadAllTextAsync(filePath, cancellationToken);
                int contentHash = GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(content);

                if (contentHash == cachedData.ContentHash)
                {
                    // Cache hit — restore without parsing
                    var script = new Script(docUri, languageId, _symbolRegistry, ScriptMode.Index, _fieldRegistry);
                    script.RestoreFromCache(cachedData, ScriptMode.Index);

                    if (script.Parsed && !script.Failed)
                    {
                        var cached = Scripts.GetOrAdd(docUri, _ => new CachedScript
                        {
                            Type = CachedScriptType.Dependency,
                            Script = script
                        });

                        // If already existed, skip
                        if (cached.Script != script && cached.Script.Parsed)
                            return true;

                        cached.LastContentHash = contentHash;
                        cached.LastParsedAt = DateTime.UtcNow;

                        // Populate global registries from restored data
                        PopulateSymbolRegistry(filePath, script);
                        PopulateFieldRegistry(filePath, script);

                        restoredFromCache = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cache restore failed for {File}, falling back to parse", filePath);
                restoredFromCache = false;
            }
        }

        if (!restoredFromCache)
        {
            // Normal parse path
            bool isNewFile = false;
            var cached = Scripts.GetOrAdd(docUri, key =>
            {
                isNewFile = true;
                return new CachedScript
                {
                    Type = CachedScriptType.Dependency,
                    Script = new Script(key, languageId, _symbolRegistry, ScriptMode.Index, _fieldRegistry)
                };
            });

            // Skip if already parsed (unless it's a new file)
            if (!isNewFile && cached.Script.Parsed)
                return false;

            await EnsureParsedAsync(docUri, cached.Script, languageId, cancellationToken);

            // Compute and store content hash for future cache saves
            try
            {
                string content = await File.ReadAllTextAsync(filePath, cancellationToken);
                cached.LastContentHash = GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(content);
            }
            catch { /* Non-fatal — hash will remain 0 */ }

            // Populate global symbol registry
            bool symbolsChanged = PopulateSymbolRegistry(filePath, cached.Script);
            cached.ExportedSymbolsChanged = symbolsChanged;
            cached.LastParsedAt = DateTime.UtcNow;

            // Populate global field registry (level.x, world.y, game.z across files)
            PopulateFieldRegistry(filePath, cached.Script);
        }

        // From here, both cache-restored and freshly-parsed files follow the same path
        if (!Scripts.TryGetValue(docUri, out var entry) || entry.Script.DefinitionsTable is null)
            return restoredFromCache;

        // Snapshot dependencies to avoid collection modification during enumeration
        var dependencies = entry.Script.Dependencies.ToList();

        // Parse and register dependencies
        foreach (Uri dep in dependencies)
        {
            await AddDependencyAsync(docUri, dep, languageId);
        }

        var indexingMode = GSCode.Parser.Configuration.CompletionConfiguration.WorkspaceIndexingMode;

        if (indexingMode == GSCode.Parser.Configuration.IndexingMode.Off)
        {
            Log.Warning("IndexFileAsync called with Off mode for {File} - this should not happen", Path.GetFileName(filePath));
            return restoredFromCache;
        }

        // Merge symbols from dependencies (filtering, deduplication, path conversion)
        var (mergeFuncLocs, mergeClassLocs) = await MergeDependencySymbolsAsync(dependencies, filePath, cancellationToken);

        // Merge definition tables (needed for go-to-definition in both modes)
        await WithAnalysisLockAsync(docUri, async () =>
        {
            if (entry.Script.DefinitionsTable is not null)
            {
                foreach (var kv in mergeFuncLocs)
                {
                    var (ns, name) = kv.Key;
                    var (fp, range) = kv.Value;
                    entry.Script.DefinitionsTable.AddFunctionLocation(ns, name, fp, TokenRange.FromRange(range));
                }
                foreach (var kv in mergeClassLocs)
                {
                    var (ns, name) = kv.Key;
                    var (fp, range) = kv.Value;
                    entry.Script.DefinitionsTable.AddClassLocation(ns, name, fp, TokenRange.FromRange(range));
                }
            }
            await Task.CompletedTask;
        });

        if (indexingMode == GSCode.Parser.Configuration.IndexingMode.Partial)
        {
            // Partial: signature analysis only (already done during parse)
            // Symbol registry populated, definition tables merged — nothing more to do
        }
        else if (indexingMode == GSCode.Parser.Configuration.IndexingMode.Full)
        {
            // Full: build exported symbols and run semantic analysis
            List<IExportedSymbol> exportedSymbols = new();
            foreach (Uri dep in dependencies)
            {
                if (Scripts.TryGetValue(dep, out CachedScript? depScript))
                    exportedSymbols.AddRange(await depScript.Script.IssueExportedSymbolsAsync(cancellationToken));
            }

            await WithAnalysisLockAsync(docUri, async () =>
            {
                await entry.Script.AnalyseAsync(exportedSymbols, cancellationToken);
            });

            // Publish diagnostics for indexed file (if LSP facade is available)
            await PublishDiagnosticsAsync(docUri, entry.Script, cancellationToken: cancellationToken);
        }

        return restoredFromCache;
    }
}

