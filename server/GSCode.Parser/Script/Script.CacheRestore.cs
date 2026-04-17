using GSCode.Parser.Cache;
using GSCode.Parser.Data;
using GSCode.Parser.SA;
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
            Sense = new ParserIntelliSense(endLine: 0, ScriptUri, LanguageId, mode);

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

            // Restore Dependencies
            foreach (var dep in cachedData.Dependencies)
            {
                DefinitionsTable.Dependencies.Add(new Uri(dep));
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

            // Restore macro definitions to the global cache
            // Note: We store macro names for reference, but don't need to fully restore
            // MacroDefinition objects since preprocessing is skipped when loading from cache.
            // The macro data is preserved in the cache for future re-serialization.

            // Mark as parsed and analysed
            Parsed = true;
            Analysed = true;

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
