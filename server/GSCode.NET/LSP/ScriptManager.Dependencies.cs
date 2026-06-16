using Serilog;
using GSCode.Data;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Data;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Concurrent;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    private async Task<Script> AddDependencyAsync(
        Uri dependentUri,
        Uri uri,
        IndexingContext? indexingContext = null,
        CancellationToken cancellationToken = default)
    {
        bool isNewDependency = false;
        string depPath = UriHelper.GetLocalPath(uri);

        // Derive language from the dependency's own file extension. GetDependencyPath in
        // ParserIntelliSense already appends the parent's language extension when resolving
        // #using paths, so a .gsc file can never produce a .csc dependency URI and vice versa.
        // Deriving from extension here is the single source of truth.
        ScriptLanguage depLanguage = ScriptLanguageExtensions.FromExtension(System.IO.Path.GetExtension(depPath));

        var cached = Scripts.GetOrAdd(uri, key =>
        {
            isNewDependency = true;
            return new CachedScript
            {
                Type = CachedScriptType.Dependency,
                Script = new Script(key, depLanguage, GetSymbolRegistry(depLanguage), ScriptMode.Index, GetFieldRegistry(depLanguage))
            };
        });

        // Only parse if new dependency or not yet parsed
        if (isNewDependency || !cached.Script.Parsed)
        {
            Log.Debug("DEPENDENCY_RESOLVE: {DependencyPath} (new={IsNew}, requested by {DependentUri})",
                depPath, isNewDependency, UriHelper.GetLocalPath(dependentUri));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string filePath = UriHelper.GetLocalPath(uri);

            if (indexingContext is not null)
            {
                FileSnapshot snapshot = await indexingContext.FileSnapshots.GetAsync(filePath, cancellationToken);
                await EnsureParsedAsync(uri, cached.Script, cancellationToken, snapshot.Content);
                cached.LastContentHash = snapshot.Exists ? snapshot.ContentHash : 0;
            }
            else
            {
                await EnsureParsedAsync(uri, cached.Script, cancellationToken);

                try
                {
                    string content = await File.ReadAllTextAsync(filePath, cancellationToken);
                    cached.LastContentHash = GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(content);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to compute content hash for dependency {Path}", filePath);
                }
            }
            cached.WorkspaceCacheDirty = true;
            _workspaceCacheDirty = true;

            // Populate global symbol registry with dependency's definitions
            bool symbolsChanged = PopulateSymbolRegistry(filePath, cached.Script);

            // Free parse-time memory (tokens, AST, analysis dictionaries) — dependency
            // scripts are not semantically analysed. Skip under Full workspace indexing,
            // where the indexer may still analyse this script and needs its AST; the
            // Full-analysis path performs its own compaction afterwards.
            if (GSCode.Parser.Configuration.CompletionConfiguration.WorkspaceIndexingMode
                != GSCode.Parser.Configuration.IndexingMode.Full)
            {
                cached.Script.CompactForSignatureIndex();
            }

            cached.ExportedSymbolsChanged = symbolsChanged;
            cached.LastParsedAt = DateTime.UtcNow;
            sw.Stop();

            Log.Debug("DEPENDENCY_RESOLVE: {DependencyPath} completed in {ElapsedMs} ms (symbolsChanged={Changed})",
                depPath, sw.ElapsedMilliseconds, symbolsChanged);
        }
        else
        {
            Log.Debug("DEPENDENCY_RESOLVE: {DependencyPath} already parsed, skipping", depPath);
        }

        cached.Dependents.TryAdd(dependentUri, 0);

        return cached.Script;
    }

    private void RemoveDependent(Uri dependentUri)
    {
        foreach (KeyValuePair<Uri, CachedScript> script in Scripts)
        {
            var dependents = script.Value.Dependents;
            if (dependents.TryRemove(dependentUri, out _))
            {
                // Housekeeping: remove orphaned dependency scripts
                if (dependents.IsEmpty && script.Value.Type == CachedScriptType.Dependency)
                {
                    Scripts.Remove(script.Key, out _);
                    CleanupLocksForUri(script.Key);
                }
            }
        }
    }

    /// <summary>
    /// Collects, filters, and deduplicates symbols from a set of dependency URIs.
    /// Returns merged function and class locations ready for DefinitionsTable.
    /// </summary>
    /// <remarks>
    /// Reads each dependency's own submissions from the global symbol registry rather than
    /// its DefinitionsTable. The table is unreliable for this purpose: it is stripped after
    /// registry population for dependency scripts, and for workspace-indexed scripts it
    /// contains locations merged from <em>their</em> dependencies — using it here would leak
    /// transitive namespaces into the editor script's KnownNamespaces, suppressing the
    /// "namespace does not exist" diagnostic for scripts that were never #using'd.
    /// </remarks>
    private (List<KeyValuePair<(string, string), (string, Range)>> Functions,
             List<KeyValuePair<(string, string), (string, Range)>> Classes)
        MergeDependencySymbols(IEnumerable<Uri> dependencies, string filePath)
    {
        string? currentWorkspaceRoot = WorkspaceBoundaryFilter.GetScriptsWorkspaceRoot(filePath);
        bool currentIsInRawFolder = WorkspaceBoundaryFilter.IsInCustomRawFolder(filePath) || WorkspaceBoundaryFilter.IsInToolsRawFolder(filePath);

        // Deduplicate inline — first-seen wins, no intermediate lists
        var uniqueFunctions = new Dictionary<(string, string), (string FilePath, Range Range)>();
        var uniqueClasses   = new Dictionary<(string, string), (string FilePath, Range Range)>();

        foreach (Uri dependency in dependencies)
        {
            string depPath = UriHelper.GetLocalPath(dependency);

            foreach (SymbolDefinition def in _symbolRegistry.GetSymbolsDefinedInFile(depPath))
            {
                if (WorkspaceBoundaryFilter.FilterSymbolLocation(def.FilePath, currentWorkspaceRoot, currentIsInRawFolder)
                    == WorkspaceBoundaryFilter.FilterResult.DifferentWorkspace)
                {
                    continue;
                }

                string? relativePath = GSCode.Parser.Util.ScriptFileResolver.ConvertToRelativeScriptPath(def.FilePath);
                if (relativePath is null)
                {
                    continue;
                }

                var key = (def.Namespace, def.Name);
                if (def.Type == ExportedSymbolType.Function)
                {
                    uniqueFunctions.TryAdd(key, (relativePath, def.Range.ToRange()));
                }
                else if (def.Type == ExportedSymbolType.Class)
                {
                    uniqueClasses.TryAdd(key, (relativePath, def.Range.ToRange()));
                }
            }
        }

        var mergeFuncLocs = uniqueFunctions
            .Select(kvp => new KeyValuePair<(string, string), (string, Range)>(kvp.Key, (kvp.Value.FilePath, kvp.Value.Range)))
            .ToList();

        var mergeClassLocs = uniqueClasses
            .Select(kvp => new KeyValuePair<(string, string), (string, Range)>(kvp.Key, (kvp.Value.FilePath, kvp.Value.Range)))
            .ToList();

        return (mergeFuncLocs, mergeClassLocs);
    }
}

