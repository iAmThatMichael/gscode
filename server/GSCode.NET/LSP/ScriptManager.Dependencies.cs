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
    private async Task<Script> AddDependencyAsync(Uri dependentUri, Uri uri)
    {
        bool isNewDependency = false;
        string depPath = UriHelper.GetLocalPath(uri);

        // Derive language from the dependency's own file extension. GetDependencyPath in
        // ParserIntelliSense already appends the parent's language extension when resolving
        // #using paths, so a .gsc file can never produce a .csc dependency URI and vice versa.
        // Deriving from extension here is the single source of truth.
        ScriptLanguage depLanguage = ScriptLanguageExtensions.FromExtension(System.IO.Path.GetExtension(depPath));

        var cached = GetScripts(depLanguage).GetOrAdd(uri, key =>
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
            //Log.Debug("[DEPENDENCY_RESOLVE] {DependencyPath} (new={IsNew}, requested by {DependentUri})",
            //    depPath, isNewDependency, UriHelper.GetLocalPath(dependentUri));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await EnsureParsedAsync(uri, cached.Script, CancellationToken.None);

            // Compute and store content hash so the cache save has a valid hash for this file
            string filePath = UriHelper.GetLocalPath(uri);
            try
            {
                string content = await File.ReadAllTextAsync(filePath);
                cached.LastContentHash = GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(content);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to compute content hash for dependency {Path}", filePath);
            }

            // Populate global symbol registry with dependency's definitions
            bool symbolsChanged = PopulateSymbolRegistry(filePath, cached.Script);

            // Location dictionaries are now redundant for dependency-only scripts — the global
            // registry serves all workspace-wide lookups from here on.
            // Do NOT strip if the script has been promoted to an editor document, because the
            // DocumentSymbolHandler reads its _functionLocations to build the outline.
            if (cached.Type != CachedScriptType.Editor)
            {
                cached.Script.StripLocationData();
            }

            cached.ExportedSymbolsChanged = symbolsChanged;
            cached.LastParsedAt = DateTime.UtcNow;
            sw.Stop();

            //Log.Debug("[DEPENDENCY_RESOLVE] {DependencyPath} completed in {ElapsedMs} ms (symbolsChanged={Changed})",
            //    depPath, sw.ElapsedMilliseconds, symbolsChanged);
        }
        else
        {
            //Log.Debug("[DEPENDENCY_RESOLVE] {DependencyPath} already parsed, skipping", depPath);
        }

        cached.Dependents.TryAdd(dependentUri, 0);

        return cached.Script;
    }

    private void RemoveDependent(Uri dependentUri)
    {
        foreach (KeyValuePair<Uri, CachedScript> script in AllScripts)
        {
            var dependents = script.Value.Dependents;
            if (dependents.TryRemove(dependentUri, out _))
            {
                // Housekeeping: remove orphaned dependency scripts
                if (dependents.IsEmpty && script.Value.Type == CachedScriptType.Dependency)
                {
                    GetScripts(script.Value.Script.Language).Remove(script.Key, out _);
                    CleanupLocksForUri(script.Key);
                }
            }
        }
    }

    /// <summary>
    /// Collects, filters, and deduplicates symbols from a set of dependency URIs.
    /// Returns merged function and class locations ready for DefinitionsTable.
    /// </summary>
    private async Task<(List<KeyValuePair<(string, string), (string, Range)>> Functions,
                        List<KeyValuePair<(string, string), (string, Range)>> Classes)>
        MergeDependencySymbolsAsync(
            IEnumerable<Uri> dependencies,
            string filePath,
            CancellationToken cancellationToken = default)
    {
        string? currentWorkspaceRoot = WorkspaceBoundaryFilter.GetScriptsWorkspaceRoot(filePath);
        bool currentIsInRawFolder = WorkspaceBoundaryFilter.IsInCustomRawFolder(filePath) || WorkspaceBoundaryFilter.IsInToolsRawFolder(filePath);

        // Deduplicate inline — first-seen wins, no intermediate lists
        var uniqueFunctions = new Dictionary<(string, string), (string FilePath, GSCode.Parser.Lexical.TokenRange Range)>();
        var uniqueClasses   = new Dictionary<(string, string), (string FilePath, GSCode.Parser.Lexical.TokenRange Range)>();

        foreach (Uri dependency in dependencies)
        {
            // Skip self-references: a script that #using's itself (e.g. array_shared.gsc uses
            // scripts\shared\array_shared) would overwrite its own SA-written absolute paths with
            // relative paths from ConvertToRelativeScriptPath, hiding all its own functions.
            string depLocalPath = UriHelper.GetLocalPath(dependency);
            if (string.Equals(depLocalPath, filePath, StringComparison.OrdinalIgnoreCase)) continue;

            ScriptLanguage depLang = ScriptLanguageExtensions.FromExtension(System.IO.Path.GetExtension(depLocalPath));
            if (!GetScripts(depLang).TryGetValue(dependency, out CachedScript? depScript)) continue;

            await WithAnalysisLockAsync(dependency, () =>
            {
                var depTable = depScript.Script.DefinitionsTable;
                if (depTable is null) return Task.CompletedTask;

                // Visit in-place under the lock — no ToList() copy
                depTable.VisitFunctionLocations((key, funcFilePath, range) =>
                {
                    if (WorkspaceBoundaryFilter.FilterSymbolLocation(funcFilePath, currentWorkspaceRoot, currentIsInRawFolder)
                        != WorkspaceBoundaryFilter.FilterResult.DifferentWorkspace)
                    {
                        string? relativePath = GSCode.Parser.Util.ScriptFileResolver.ConvertToRelativeScriptPath(funcFilePath);
                        if (relativePath != null)
                            uniqueFunctions.TryAdd((key.Qualifier, key.SymbolName), (relativePath, range));
                    }
                });

                depTable.VisitClassLocations((key, classFilePath, range) =>
                {
                    if (WorkspaceBoundaryFilter.FilterSymbolLocation(classFilePath, currentWorkspaceRoot, currentIsInRawFolder)
                        != WorkspaceBoundaryFilter.FilterResult.DifferentWorkspace)
                    {
                        string? relativePath = GSCode.Parser.Util.ScriptFileResolver.ConvertToRelativeScriptPath(classFilePath);
                        if (relativePath != null)
                            uniqueClasses.TryAdd((key.Qualifier, key.SymbolName), (relativePath, range));
                    }
                });

                return Task.CompletedTask;
            }, cancellationToken);
        }

        // ToRange() called only on unique survivors
        var mergeFuncLocs = uniqueFunctions
            .Select(kvp => new KeyValuePair<(string, string), (string, Range)>(kvp.Key, (kvp.Value.FilePath, kvp.Value.Range.ToRange())))
            .ToList();

        var mergeClassLocs = uniqueClasses
            .Select(kvp => new KeyValuePair<(string, string), (string, Range)>(kvp.Key, (kvp.Value.FilePath, kvp.Value.Range.ToRange())))
            .ToList();

        return (mergeFuncLocs, mergeClassLocs);
    }
}

