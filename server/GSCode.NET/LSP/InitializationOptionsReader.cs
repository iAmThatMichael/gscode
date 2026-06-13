using GSCode.Parser.Configuration;
using Newtonsoft.Json.Linq;
using Serilog.Events;

namespace GSCode.NET.LSP;

/// <summary>
/// Reads typed values from LSP initialization options.
/// StreamJsonRpc delivers <c>InitializationOptions</c> as a Newtonsoft.Json <see cref="JToken"/>.
/// </summary>
internal static class InitializationOptionsReader
{
    private static JToken? GetGsCodeSection(JToken? initOptions)
        => initOptions?["gscode"];

    public static LogEventLevel ParseServerLogLevel(JToken? initOptions)
    {
        var value = GetGsCodeSection(initOptions)?["serverLogLevel"]?.Value<string>()?.ToLowerInvariant();
        return value switch
        {
            "messages" => LogEventLevel.Information,
            "verbose"  => LogEventLevel.Debug,
            _          => LogEventLevel.Warning   // "off" or unset → suppress to warnings only
        };
    }

    public static IndexingMode ParseWorkspaceIndexingMode(JToken? initOptions)
    {
        var value = GetGsCodeSection(initOptions)?["workspaceIndexingMode"]?.Value<string>()?.ToLowerInvariant();
        return value switch
        {
            "partial" => IndexingMode.Partial,
            "full"    => IndexingMode.Full,
            _         => IndexingMode.Off
        };
    }

    public static string? ParseCustomRawPath(JToken? initOptions)
    {
        var value = GetGsCodeSection(initOptions)?["customRawPath"]?.Value<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool ParseAllowRawFolderWrites(JToken? initOptions)
        => GetGsCodeSection(initOptions)?["allowRawFolderWrites"]?.Value<bool>() ?? false;

    public static bool ParseEnableWorkspaceCache(JToken? initOptions)
        => GetGsCodeSection(initOptions)?["enableWorkspaceCache"]?.Value<bool>() ?? false;

    public static bool ParseIndexGameScripts(JToken? initOptions)
        => GetGsCodeSection(initOptions)?["indexGameScripts"]?.Value<bool>() ?? true;

    public static RawFileWarningMode ParseRawFileWarningMode(JToken? initOptions)
        => ParseRawFileWarningModeValue(GetGsCodeSection(initOptions)?["rawFileWarningMode"]?.Value<string>());

    /// <summary>
    /// Shared string→enum mapping for the raw-file warning mode, used by both the
    /// initialization options and workspace/didChangeConfiguration paths.
    /// </summary>
    public static RawFileWarningMode ParseRawFileWarningModeValue(string? value)
        => value?.ToLowerInvariant() switch
        {
            "off"   => RawFileWarningMode.Off,
            "all"   => RawFileWarningMode.All,
            _       => RawFileWarningMode.Stock
        };
}