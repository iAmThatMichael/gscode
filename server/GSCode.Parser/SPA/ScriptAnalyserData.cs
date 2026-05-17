using GSCode.Data;
using GSCode.Parser.SPA.Models;
using System.Text.Json;
using System.Collections.Concurrent;
using Serilog;

namespace GSCode.Parser.SPA;

public class ScriptAnalyserData
{
    public string GameId { get; } = "t7";
    public ScriptLanguage Language { get; }

    public ScriptAnalyserData(ScriptLanguage language)
    {
        Language = language;
    }

    private static readonly ConcurrentDictionary<ScriptLanguage, ScrLibraryData> _languageLibraries = new();

    /// <summary>
    /// Static cache of ScriptAnalyserData instances by language.
    /// This eliminates redundant instance creation across scripts.
    /// </summary>
    private static readonly Dictionary<ScriptLanguage, ScriptAnalyserData> _sharedInstances = new();
    private static readonly object _sharedInstancesLock = new();

    /// <summary>
    /// Shared STJ options: case-insensitive matching preserves Newtonsoft's lenient default
    /// behaviour when deserialising camelCase JSON API responses.
    /// </summary>
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Gets a shared ScriptAnalyserData instance for the specified language.
    /// </summary>
    public static ScriptAnalyserData? GetShared(ScriptLanguage language)
    {
        lock (_sharedInstancesLock)
        {
            if (_sharedInstances.TryGetValue(language, out var existing))
                return existing;

            if (!_languageLibraries.ContainsKey(language))
                return null;

            var instance = new ScriptAnalyserData(language);
            _sharedInstances[language] = instance;
            return instance;
        }
    }

    /// <summary>
    /// Checks if a language API library is loaded and available.
    /// </summary>
    public static bool IsLanguageLoaded(ScriptLanguage language) =>
        _languageLibraries.ContainsKey(language);

    public static async Task<bool> LoadLanguageApiAsync(string url, string filePathFallback)
    {
        bool loaded = false;

        // Load local file first so we have a baseline revision to compare against.
        try
        {
            string json = File.ReadAllText(filePathFallback);
            loaded = LoadLanguageApiData(json);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load API from {FilePath}", filePathFallback);
        }

        // Try to load from URL — will only replace local if it has a higher revision.
        try
        {
            using HttpClient client = new();
            string json = await client.GetStringAsync(url);
            loaded = LoadLanguageApiData(json) || loaded;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load API from {Url}", url);
        }

        return loaded;
    }

    private static bool LoadLanguageApiData(string source)
    {
        try
        {
            ScriptApiJsonLibrary? library = JsonSerializer.Deserialize<ScriptApiJsonLibrary>(source, _jsonOptions);

            if (library is null)
            {
                Log.Error("Failed to deserialize API library: result was null.");
                return false;
            }

            // Parse the JSON string language ID to the enum at the boundary.
            // FromString defaults to Gsc for unrecognised values, so log a warning if the value is unexpected.
            ScriptLanguage language = ScriptLanguageExtensions.FromString(library.LanguageId);
            if (!library.LanguageId.Equals(language.ToLanguageId(), StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Unrecognised languageId '{LanguageId}' in API library; defaulting to GSC.", library.LanguageId);
            }

            // All built-ins are implicit, because they can be called without using the sys namespace.
            foreach (ScrFunction function in library.Api)
            {
                function.Namespace = "sys";
                function.Implicit = true;
                function.IsBuiltIn = true;

                // Check for vararg parameters and set the Vararg flag on the overload
                foreach (ScrFunctionOverload overload in function.Overloads)
                {
                    if (overload.Parameters.Any(p => p.Type?.DataType?.Equals("vararg", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        overload.Vararg = true;
                    }
                }
            }

            if (_languageLibraries.TryGetValue(language, out ScrLibraryData? existingLibrary)
                && existingLibrary!.Library.Revision >= library.Revision)
            {
                return false;
            }

            _languageLibraries[language] = new ScrLibraryData(library);
            Log.Information("Loaded API library for {Language}.", language);
            return true;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to deserialize API library.");
        }
        return false;
    }

    public List<ScrFunction> GetApiFunctions(string? filter = null)
    {
        if (!_languageLibraries.TryGetValue(Language, out ScrLibraryData? library))
        {
            Log.Error("No API library found for {Language}", Language);
            return [];
        }
        Log.Information("API library found for {Language}, it has {Count} functions", Language, library.Functions.Count);
        return library.Library.Api;
    }

    public ScrFunction? GetApiFunction(string name)
    {
        if (!_languageLibraries.TryGetValue(Language, out ScrLibraryData? library))
        {
            Log.Error("No API library found for {Language}", Language);
            return null;
        }
        return library.Functions.TryGetValue(name, out ScrFunction? function) ? function : null;
    }
}

internal class ScrLibraryData
{
    public ScriptApiJsonLibrary Library { get; }
    public SortedList<string, ScrFunction> Functions { get; }

    public ScrLibraryData(ScriptApiJsonLibrary library)
    {
        Library = library;
        Functions = new SortedList<string, ScrFunction>(library.Api.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ScrFunction function in library.Api)
        {
            Functions.Add(function.Name, function);
        }
    }
}