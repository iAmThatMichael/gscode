using System.Reflection;
using Serilog;

namespace GSCode.NET.LSP;

/// <summary>
/// Knows which script files shipped with the Black Ops III mod tools (the stock contents of
/// <c>share/raw</c>). The list is embedded as a resource — the stock script set never changes —
/// and is used to warn only about edits to stock scripts, leaving user-owned scripts kept in
/// the raw folder alone.
/// </summary>
public static class StockScripts
{
    private const string ResourceName = "GSCode.NET.Resources.t7_stock_scripts.txt";

    private static readonly Lazy<HashSet<string>> s_stockPaths = new(LoadStockPaths);

    private static HashSet<string> LoadStockPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                Log.Warning("Stock script list resource '{Resource}' not found — stock detection disabled", ResourceName);
                return result;
            }

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                result.Add(Normalize(line));
            }

            Log.Debug("Loaded {Count} stock script paths", result.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load stock script list");
        }

        return result;
    }

    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Returns true when <paramref name="relativePath"/> (a path relative to a raw root,
    /// e.g. <c>scripts\shared\util_shared.gsc</c>) is a script that shipped with the mod tools.
    /// </summary>
    public static bool IsStockScript(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        return s_stockPaths.Value.Contains(Normalize(relativePath));
    }
}
