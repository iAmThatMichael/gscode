using GSCode.Parser.Cache;
using GSCode.Parser.Data;
using GSCode.Parser.SA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;

namespace GSCode.Parser;

public partial class Script
{
    /// <summary>
    /// Restores this script from cached parse results without running the lexer, parser, or analyser.
    /// Sets Parsed and Analysed to true, populates DefinitionsTable, and registers completion source tasks.
    /// </summary>
    /// <param name="cachedData">The cached script data to restore from.</param>
    /// <param name="mode">The mode this script is being restored in (Editor or Index).</param>
    public void RestoreFromCache(CachedScriptData cachedData, ScriptMode mode)
    {
        try
        {
            // Create a minimal ParserIntelliSense for this restored script
            // In Index mode, we don't need tokens or most IntelliSense features
            Sense = new ParserIntelliSense(endLine: 0, ScriptUri, Language, mode);

            // Restore DefinitionsTable
            DefinitionsTable = new DefinitionsTable(cachedData.CurrentNamespace, GlobalSymbolProvider);

            // Populate ExportedFunctions and ExportedClasses
            foreach (var func in cachedData.ExportedFunctions)
            {
                DefinitionsTable.ExportedFunctions.Add(func);
                DefinitionsTable.ExportedSymbols[func.Name] = func;
            }

            foreach (var cls in cachedData.ExportedClasses)
            {
                DefinitionsTable.ExportedClasses.Add(cls);
                DefinitionsTable.ExportedSymbols[cls.Name] = cls;
            }

            // Restore UsingPaths
            foreach (var dep in cachedData.Dependencies)
            {
                var uri = new Uri(dep);
                DefinitionsTable.UsingPaths.Add(uri);
                _usingPaths.Add(uri);
            }

            // Restore InsertPaths
            if (cachedData.InsertDependencies is { Count: > 0 })
            {
                _insertPaths.AddRange(cachedData.InsertDependencies);
            }

            // Restore function locations, parameters, flags, and docs
            foreach (var kvp in cachedData.FunctionLocations)
            {
                DefinitionsTable.AddFunctionLocation(
                    kvp.Key.Qualifier,
                    kvp.Key.SymbolName,
                    kvp.Value.FilePath,
                    kvp.Value.Range
                );
            }

            foreach (var kvp in cachedData.ClassLocations)
            {
                DefinitionsTable.AddClassLocation(
                    kvp.Key.Qualifier,
                    kvp.Key.SymbolName,
                    kvp.Value.FilePath,
                    kvp.Value.Range
                );
            }

            foreach (var kvp in cachedData.FunctionParameters)
            {
                DefinitionsTable.RecordFunctionParameters(
                    kvp.Key.Qualifier,
                    kvp.Key.SymbolName,
                    kvp.Value
                );
            }

            foreach (var kvp in cachedData.FunctionFlags)
            {
                DefinitionsTable.RecordFunctionFlags(
                    kvp.Key.Qualifier,
                    kvp.Key.SymbolName,
                    kvp.Value
                );
            }

            foreach (var kvp in cachedData.FunctionDocs)
            {
                if (kvp.Value is not null)
                {
                    DefinitionsTable.RecordFunctionDoc(
                        kvp.Key.Qualifier,
                        kvp.Key.SymbolName,
                        kvp.Value
                    );
                }
            }

            // Re-register macro source paths into MacroDefinitionCache so that
            // GetDetailedStatistics() reports accurate GSH file and macro counts after
            // a cache restore (preprocessing is skipped, so the cache would otherwise
            // have no entries for macros that came from #insert'd GSH files).
            // Re-register macro source paths into MacroDefinitionCache so that
            // GetDetailedStatistics() reports accurate GSH file and macro counts after
            // a cache restore (preprocessing is skipped, so the cache would otherwise
            // have no entries for macros that came from #insert'd GSH files).
            // Also preserve the paths on the script so that a subsequent cache re-save
            // (ExtractCacheData) can round-trip them without zeroing out macro data.
            foreach (var (macroName, sourceFilePath) in cachedData.MacroDefinitions)
            {
                Pre.MacroDefinitionCache.Instance.TrackMacroSource(sourceFilePath, macroName);
            }
            _cachedMacroSourcePaths = cachedData.MacroDefinitions.Count > 0
                ? new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(
                    new Dictionary<string, string?>(cachedData.MacroDefinitions, StringComparer.OrdinalIgnoreCase))
                : null;

            // Mark as parsed and analysed
            Parsed = true;
            Analysed = true;

            // Restore diagnostics into Sense so GetDiagnosticsAsync returns them correctly
            foreach (var cd in cachedData.Diagnostics)
            {
                Sense.Diagnostics.Add(new Diagnostic
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        cd.StartLine, cd.StartChar,
                        cd.EndLine, cd.EndChar),
                    Severity = cd.Severity,
                    Code = cd.Code is not null ? new OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticCode(cd.Code) : (OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticCode?)null,
                    Message = cd.Message,
                    Source = cd.Source
                });
            }

            // Set tasks to completed so WaitUntilParsedAsync/WaitUntilAnalysedAsync don't NPE
            ParsingTask = Task.CompletedTask;
            AnalysisTask = Task.CompletedTask;

            // Complete the TaskCompletionSources so any awaiting code can proceed
            _parseInitiated.TrySetResult();
            _analysisInitiated.TrySetResult();

            Log.Debug("Restored script {Uri} from cache (namespace: {Namespace}, functions: {FuncCount}, classes: {ClassCount})",
                ScriptUri.LocalPath, cachedData.CurrentNamespace,
                cachedData.ExportedFunctions.Count, cachedData.ExportedClasses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore script {Uri} from cache, will fall back to normal parsing", ScriptUri.LocalPath);
            // Reset state and let caller fall back to normal parsing
            Failed = true;
            Parsed = false;
            Analysed = false;
            DefinitionsTable = null;
        }
    }
}
