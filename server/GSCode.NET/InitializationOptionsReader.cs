using GSCode.Parser.Configuration;
using Newtonsoft.Json.Linq;
using Serilog.Events;

namespace GSCode.NET;

/// <summary>
/// Reads typed values from LSP initialization options.
/// <para>
/// OmniSharp delivers <c>InitializationOptions</c> as a Newtonsoft.Json <see cref="JToken"/>,
/// so this helper centralises all Newtonsoft.Json usage that cannot be replaced with
/// System.Text.Json until OmniSharp is migrated away from Newtonsoft (see Phase 5 #21).
/// </para>
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
}