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
                ? await WorkspaceCacheManager.LoadAsync(cacheFilePath, CurrentServerVersion)
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
            int missNotInCache = 0;
            int missHashMismatch = 0;
            int missRestoreFailed = 0;

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
                        var result = await IndexFileAsync(file, rootDirectory, cancellationToken);
                        switch (result)
                        {
                            case CacheResult.Hit:               Interlocked.Increment(ref cacheHits); break;
                            case CacheResult.MissNotInCache:    Interlocked.Increment(ref missNotInCache); break;
                            case CacheResult.MissHashMismatch:  Interlocked.Increment(ref missHashMismatch); break;
                            case CacheResult.MissRestoreFailed: Interlocked.Increment(ref missRestoreFailed); break;
                        }
#if DEBUG
                        fileSw.Stop();
                        Log.Information("{Status} {File} in {ElapsedMs} ms",
                            result == CacheResult.Hit ? "Restored" : "Indexed", rel, fileSw.ElapsedMilliseconds);
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

            if (UseWorkspaceCache && (missNotInCache > 0 || missHashMismatch > 0 || missRestoreFailed > 0))
            {
                Log.Information("Cache miss breakdown — new/uncached: {NotInCache}, changed: {HashMismatch}, restore error: {RestoreFailed}",
                    missNotInCache, missHashMismatch, missRestoreFailed);
            }

            // Save updated cache to disk
            if (UseWorkspaceCache)
            {
                await SaveWorkspaceCacheAsync();
            }

            // Signal that all files (and their #insert'd GSH macros) are now in the cache.
            IsIndexingComplete = true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Indexing cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Indexing failed for {Root}", rootDirectory);
        }
        finally
        {
            // Release the loaded cache object — all data has been restored into live Script
            // objects and re-saved to disk (full path), or indexing was interrupted (partial/failed
            // paths).
            _workspaceCache = null;

            // Release per-script cached macro path dictionaries — these were only needed during
            // cache restore and the cache re-save. Both uses are now complete (or will never run),
            // so null them out to free the string-pair entries.
            foreach (var kv in Scripts)
                kv.Value.Script.ReleaseCachedMacroPaths();

            // Release parse and analysis semaphores for indexed files. Editor-opened files have
            // their locks cleaned up via RemoveEditor; indexed-only files are never removed that
            // way, so without this they leak one SemaphoreSlim per file for the server lifetime.
            foreach (var kv in Scripts)
                CleanupLocksForUri(kv.Key);
        }
    }

    private enum CacheResult { Hit, MissNotInCache, MissHashMismatch, MissRestoreFailed, MissNoCache }

    /// <summary>
    /// Indexes a single file. Returns the cache result for telemetry.
    /// </summary>
    private async Task<CacheResult> IndexFileAsync(string filePath, string rootDirectory, CancellationToken cancellationToken)
    {
        string ext = Path.GetExtension(filePath);
        string languageId = string.Equals(ext, ".csc", StringComparison.OrdinalIgnoreCase) ? "csc" : "gsc";
        string relPath = Path.GetRelativePath(rootDirectory, filePath);

        // Normalize path so drive-letter casing (G:\ vs g:\) never causes a cache miss
        filePath = Path.GetFullPath(filePath);

        Uri docUri = new Uri(filePath);

        // Try to restore from persistent cache before parsing
        CacheResult cacheResult;
        bool needsAnalysis = false;
        if (_workspaceCache is null)
        {
            cacheResult = CacheResult.MissNoCache;
        }
        else if (!_workspaceCache.Scripts.TryGetValue(filePath, out var cachedData))
        {
            Log.Debug("CACHE_MISS (not_in_cache): {File} | full path: {FullPath}", relPath, filePath);
            cacheResult = CacheResult.MissNotInCache;
        }
        else
        {
            try
            {
                // Read file content and compute hash
                string content = await File.ReadAllTextAsync(filePath, cancellationToken);
                int contentHash = GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(content);

                if (contentHash != cachedData.ContentHash)
                {
                    Log.Debug("CACHE_MISS (hash_mismatch): {File} | full path: {FullPath} | disk hash: {DiskHash} | cached hash: {CachedHash}",
                        relPath, filePath, contentHash, cachedData.ContentHash);
                    cacheResult = CacheResult.MissHashMismatch;
                }
                else
                {
                    // Hash matches — attempt restore
                    var script = new Script(docUri, languageId, _symbolRegistry, ScriptMode.Index, _fieldRegistry);
                    script.RestoreFromCache(cachedData, ScriptMode.Index);

                    if (script.Parsed && !script.Failed)
                    {
                        var cached = Scripts.GetOrAdd(docUri, _ => new CachedScript
                        {
                            Type = CachedScriptType.Dependency,
                            Script = script
                        });

                        if (cached.Script != script)
                        {
                            // Another task already registered this URI — our cache-restored script
                            // object is discarded; work with the already-registered one instead.
                            if (cached.Script.Analysed)
                            {
                                // Fully analysed — another indexer task won the race and will publish.
                                Log.Debug("CACHE_HIT (race_won): {File} — another task already registered and analysed this URI", relPath);
                                return CacheResult.Hit;
                            }
                            if (cached.Script.Parsed)
                            {
                                // Already parsed (e.g. AddDependencyAsync ran first). The work is done —
                                // populate registries from the winning script and fall through so analysis
                                // runs below and generates fresh diagnostics for this file.
                                Log.Debug("CACHE_HIT (race_parsed): {File} — URI already parsed by dependency resolution, reusing", relPath);
                                cached.LastContentHash = contentHash;
                                cached.LastParsedAt = DateTime.UtcNow;
                                PopulateSymbolRegistry(filePath, cached.Script);
                                PopulateFieldRegistry(filePath, cached.Script);
                                cacheResult = CacheResult.Hit;
                                needsAnalysis = true;
                            }
                            else
                            {
                                // Registered but not yet parsed — wait for the in-progress parse
                                // (AddDependencyAsync holds EnsureParsedAsync) then reuse.
                                Log.Debug("CACHE_HIT (race_wait): {File} — waiting for in-progress parse to complete", relPath);
                                await EnsureParsedAsync(docUri, cached.Script, languageId, cancellationToken);
                                cached.LastContentHash = contentHash;
                                cached.LastParsedAt = DateTime.UtcNow;
                                PopulateSymbolRegistry(filePath, cached.Script);
                                PopulateFieldRegistry(filePath, cached.Script);
                                cacheResult = CacheResult.Hit;
                                needsAnalysis = true;
                            }

                        }
                        else
                        {
                            cached.LastContentHash = contentHash;
                            cached.LastParsedAt = DateTime.UtcNow;

                            // Populate global registries from restored data
                            PopulateSymbolRegistry(filePath, script);
                            PopulateFieldRegistry(filePath, script);

                            Log.Debug("CACHE_HIT: {File}", relPath);
                            cacheResult = CacheResult.Hit;
                        }
                    }
                    else
                    {
                        Log.Debug("CACHE_MISS (restore_failed): {File} — RestoreFromCache returned Parsed={Parsed} Failed={Failed}",
                            relPath, script.Parsed, script.Failed);
                        cacheResult = CacheResult.MissRestoreFailed;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cache restore failed for {File}, falling back to parse", relPath);
                cacheResult = CacheResult.MissRestoreFailed;
            }
        }

        if (cacheResult != CacheResult.Hit)
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

            // If already parsed (e.g. loaded as a dependency of an editor-open file), skip
            // re-parsing but still fall through so diagnostics are published below.
            bool alreadyParsed = !isNewFile && cached.Script.Parsed;
            if (alreadyParsed)
            {
                cacheResult = CacheResult.MissNotInCache;
            }
            else
            {
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
        }

        // From here, both cache-restored and freshly-parsed files follow the same path
        if (!Scripts.TryGetValue(docUri, out var entry) || entry.Script.DefinitionsTable is null)
            return cacheResult;

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
            return cacheResult;
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
        if (indexingMode == GSCode.Parser.Configuration.IndexingMode.Full)
        {
            // Full: build exported symbols and run semantic analysis (skip for cache hits — already analysed,
            // unless needsAnalysis is set because a dependency-race script needs fresh diagnostics).
            if (cacheResult != CacheResult.Hit || needsAnalysis)
            {
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
            }

            // Publish diagnostics for indexed file (if LSP facade is available) — covers both
            // freshly-parsed files and cache-restored files (whose diagnostics are restored into Sense).
            await PublishDiagnosticsAsync(docUri, entry.Script, cancellationToken: cancellationToken);
        }

        return cacheResult;
    }

}
