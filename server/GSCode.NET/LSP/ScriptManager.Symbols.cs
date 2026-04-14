using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.SA;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System.IO;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    /// <summary>
    /// Populates the global symbol registry with definitions from a parsed script.
    /// Returns true if exported symbols changed (requiring dependent re-analysis).
    /// </summary>
    private bool PopulateSymbolRegistry(string filePath, Script script)
    {
        if (script.DefinitionsTable is null)
            return false;

        var defTable = script.DefinitionsTable;
        var currentNamespace = defTable.CurrentNamespace;

        var newSymbols = new List<SymbolDefinition>();

        foreach (var func in defTable.ExportedFunctions)
        {
            var loc = defTable.GetFunctionLocation(func.Namespace, func.Name);
            // Function flags (e.g. "autoexec", "private") are recorded on the DefinitionsTable
            // by SignatureAnalyser via RecordFunctionFlags, not on the ScrFunction instance itself,
            // so we read them from the table here.
            var flags = defTable.GetFunctionFlags(func.Namespace, func.Name);
            newSymbols.Add(new SymbolDefinition(
                Namespace: func.Namespace,
                Name: func.Name,
                Type: ExportedSymbolType.Function,
                FilePath: loc?.FilePath ?? filePath,
                Range: loc?.Range ?? default,
                Parameters: func.Overloads.FirstOrDefault()?.Parameters?.Select(p => p.Name).ToArray(),
                Flags: flags,
                Documentation: func.DocComment ?? func.Description,
                Symbol: func
            ));
        }

        // Note: ScrClass doesn't carry a Namespace property; use the script's current namespace
        foreach (var cls in defTable.ExportedClasses)
        {
            var loc = defTable.GetClassLocation(currentNamespace, cls.Name);
            newSymbols.Add(new SymbolDefinition(
                Namespace: currentNamespace,
                Name: cls.Name,
                Type: ExportedSymbolType.Class,
                FilePath: loc?.FilePath ?? filePath,
                Range: loc?.Range ?? default,
                Documentation: cls.Description,
                Symbol: cls
            ));
        }

        return _symbolRegistry.UpdateSymbolsForFile(filePath, newSymbols);
    }

    /// <summary>
    /// Finds a symbol (function or class) by optional namespace and name.
    /// Tries the global registry first (O(1)), falls back to per-script lookup.
    /// </summary>
    public Location? FindSymbolLocation(string? ns, string name, string? currentFilePath = null)
    {
        var symbol = _symbolRegistry.FindSymbol(ns, name);
        if (symbol is not null)
        {
            string? resolvedPath = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, symbol.FilePath);
            if (resolvedPath != null && File.Exists(resolvedPath))
                return new Location() { Uri = new Uri(resolvedPath), Range = symbol.Range.ToRange() };
        }

        // Fallback to per-script lookup for symbols not yet in the registry
        foreach (KeyValuePair<Uri, CachedScript> kvp in Scripts)
        {
            CachedScript cached = kvp.Value;
            if (cached.Script.DefinitionsTable is null) continue;

            if (ns is not null)
            {
                var funcLoc = cached.Script.DefinitionsTable.GetFunctionLocation(ns, name);
                if (funcLoc is not null)
                {
                    string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, funcLoc.Value.FilePath);
                    if (resolved != null && File.Exists(resolved))
                        return new Location() { Uri = new Uri(resolved), Range = funcLoc.Value.Range.ToRange() };
                }

                var classLoc = cached.Script.DefinitionsTable.GetClassLocation(ns, name);
                if (classLoc is not null)
                {
                    string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, classLoc.Value.FilePath);
                    if (resolved != null && File.Exists(resolved))
                        return new Location() { Uri = new Uri(resolved), Range = classLoc.Value.Range.ToRange() };
                }
            }

            var funcAny = cached.Script.DefinitionsTable.GetFunctionLocationAnyNamespace(name);
            if (funcAny is not null)
            {
                string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, funcAny.Value.FilePath);
                if (resolved != null && File.Exists(resolved))
                    return new Location() { Uri = new Uri(resolved), Range = funcAny.Value.Range.ToRange() };
            }

            var classAny = cached.Script.DefinitionsTable.GetClassLocationAnyNamespace(name);
            if (classAny is not null)
            {
                string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, classAny.Value.FilePath);
                if (resolved != null && File.Exists(resolved))
                    return new Location() { Uri = new Uri(resolved), Range = classAny.Value.Range.ToRange() };
            }
        }

        return null;
    }

    /// <summary>Gets the total number of loaded scripts (editor and dependency).</summary>
    public int GetLoadedScriptCount() => Scripts.Count;

    /// <summary>Gets per-extension script counts.</summary>
    public (int GscFiles, int CscFiles) GetScriptCountsByType()
    {
        int gscCount = 0;
        int cscCount = 0;

        foreach (var kvp in Scripts)
        {
            string filePath = UriHelper.GetLocalPath(kvp.Key);
            if (filePath.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase))
                gscCount++;
            else if (filePath.EndsWith(".csc", StringComparison.OrdinalIgnoreCase))
                cscCount++;
        }

        return (gscCount, cscCount);
    }

    public IEnumerable<LoadedScript> GetLoadedScripts()
    {
        foreach (var kv in Scripts)
            yield return new LoadedScript(kv.Key, kv.Value.Script);
    }
}

