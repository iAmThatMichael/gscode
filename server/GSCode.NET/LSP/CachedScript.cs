using GSCode.Parser;
using System.Collections.Concurrent;

namespace GSCode.NET.LSP;

public enum CachedScriptType
{
    Editor,
    Dependency
}

public class CachedScript
{
    public CachedScriptType Type { get; init; }
    // Thread-safe set of dependents
    public ConcurrentDictionary<Uri, byte> Dependents { get; } = new(UriComparer.OrdinalIgnoreCase);
    public required Script Script { get; init; }

    /// <summary>
    /// Hash of the last parsed content. Used to detect if content actually changed.
    /// </summary>
    public int LastContentHash { get; set; } = 0;

    /// <summary>
    /// Hash of exported symbols from last parse. Used to detect if dependents need re-analysis.
    /// </summary>
    public int LastExportedSymbolsHash { get; set; } = 0;

    /// <summary>
    /// Timestamp of last successful parse.
    /// </summary>
    public DateTime LastParsedAt { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Whether exported symbols changed during the last parse, requiring dependent re-analysis.
    /// </summary>
    public bool ExportedSymbolsChanged { get; set; } = true;
}

public readonly record struct LoadedScript(Uri Uri, Script Script);
