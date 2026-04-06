using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Data;
using OmniSharp.Extensions.LanguageServer.Protocol;
using System.Collections.Concurrent;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    private async Task<Script> AddDependencyAsync(Uri dependentUri, Uri uri, string languageId)
    {
        var docUri = DocumentUri.From(uri);
        bool isNewDependency = false;

        var cached = Scripts.GetOrAdd(docUri, key =>
        {
            isNewDependency = true;
            return new CachedScript
            {
                Type = CachedScriptType.Dependency,
                Script = new Script(key, languageId, _symbolRegistry, ScriptMode.Index)
            };
        });

        // Only parse if new dependency or not yet parsed
        if (isNewDependency || !cached.Script.Parsed)
        {
            await EnsureParsedAsync(docUri, cached.Script, languageId, CancellationToken.None);

            // Populate global symbol registry with dependency's definitions
            string filePath = uri.LocalPath;
            bool symbolsChanged = PopulateSymbolRegistry(filePath, cached.Script);

            cached.ExportedSymbolsChanged = symbolsChanged;
            cached.LastParsedAt = DateTime.UtcNow;
        }

        cached.Dependents.TryAdd(DocumentUri.From(dependentUri), 0);

        return cached.Script;
    }

    private void RemoveDependent(DocumentUri dependentUri)
    {
        foreach (KeyValuePair<DocumentUri, CachedScript> script in Scripts)
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
    private async Task<(List<KeyValuePair<(string, string), (string, Range)>> Functions,
                        List<KeyValuePair<(string, string), (string, Range)>> Classes)>
        MergeDependencySymbolsAsync(
            IEnumerable<Uri> dependencies,
            string filePath,
            CancellationToken cancellationToken = default)
    {
        string? currentWorkspaceRoot = WorkspaceBoundaryFilter.GetScriptsWorkspaceRoot(filePath);
        bool currentIsInRawFolder = WorkspaceBoundaryFilter.IsInCustomRawFolder(filePath) || WorkspaceBoundaryFilter.IsInToolsRawFolder(filePath);

        var allFuncLocs = new List<(string Namespace, string Name, string FilePath, Range Range)>();
        var allClassLocs = new List<(string Namespace, string Name, string FilePath, Range Range)>();

        foreach (Uri dependency in dependencies)
        {
            var depDoc = DocumentUri.From(dependency);
            if (!Scripts.TryGetValue(depDoc, out CachedScript? depScript)) continue;

            await WithAnalysisLockAsync(depDoc, async () =>
            {
                var depTable = depScript.Script.DefinitionsTable;
                if (depTable is null) return;

                foreach (var funcLoc in depTable.GetAllFunctionLocations())
                {
                    string funcFilePath = funcLoc.Value.FilePath;
                    if (WorkspaceBoundaryFilter.FilterSymbolLocation(funcFilePath, currentWorkspaceRoot, currentIsInRawFolder)
                        != WorkspaceBoundaryFilter.FilterResult.DifferentWorkspace)
                    {
                        string? relativePath = GSCode.Parser.Util.ScriptFileResolver.ConvertToRelativeScriptPath(funcFilePath);
                        if (relativePath != null)
                            allFuncLocs.Add((funcLoc.Key.Qualifier, funcLoc.Key.SymbolName, relativePath, funcLoc.Value.Range.ToRange()));
                    }
                }

                foreach (var classLoc in depTable.GetAllClassLocations())
                {
                    string classFilePath = classLoc.Value.FilePath;
                    if (WorkspaceBoundaryFilter.FilterSymbolLocation(classFilePath, currentWorkspaceRoot, currentIsInRawFolder)
                        != WorkspaceBoundaryFilter.FilterResult.DifferentWorkspace)
                    {
                        string? relativePath = GSCode.Parser.Util.ScriptFileResolver.ConvertToRelativeScriptPath(classFilePath);
                        if (relativePath != null)
                            allClassLocs.Add((classLoc.Key.Qualifier, classLoc.Key.SymbolName, relativePath, classLoc.Value.Range.ToRange()));
                    }
                }

                await Task.CompletedTask;
            }, cancellationToken);
        }

        // Deduplicate: first-seen wins
        var uniqueFunctions = new Dictionary<(string Namespace, string Name), (string FilePath, Range Range)>();
        var uniqueClasses = new Dictionary<(string Namespace, string Name), (string FilePath, Range Range)>();

        foreach (var func in allFuncLocs)
            uniqueFunctions.TryAdd((func.Namespace, func.Name), (func.FilePath, func.Range));

        foreach (var cls in allClassLocs)
            uniqueClasses.TryAdd((cls.Namespace, cls.Name), (cls.FilePath, cls.Range));

        var mergeFuncLocs = uniqueFunctions
            .Select(kvp => new KeyValuePair<(string, string), (string, Range)>(kvp.Key, kvp.Value))
            .ToList();

        var mergeClassLocs = uniqueClasses
            .Select(kvp => new KeyValuePair<(string, string), (string, Range)>(kvp.Key, kvp.Value))
            .ToList();

        return (mergeFuncLocs, mergeClassLocs);
    }
}
