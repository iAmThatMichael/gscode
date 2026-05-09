using GSCode.Parser.Configuration;
using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GSCode.NET.LSP.Handlers;

internal class ConfigurationHandler(ScriptManager scriptManager, LoggingLevelSwitch loggingLevelSwitch) : DidChangeConfigurationHandlerBase
{
    public override Task<Unit> Handle(DidChangeConfigurationParams request, CancellationToken cancellationToken)
    {
        try
        {
            var settings = request.Settings as JToken;
            var gscodeSection = settings?["gscode"];
            if (gscodeSection is null) return Unit.Task;

            var opts = CompletionOptions.Current;

            // customRawPath — normalise empty strings to null, sync to ScriptManager
            var customRawPath = gscodeSection["customRawPath"]?.Value<string>();
            customRawPath = string.IsNullOrWhiteSpace(customRawPath) ? null : customRawPath;
            opts.CustomRawPath = customRawPath;
            scriptManager.CustomRawPath = customRawPath;

            // allowRawFolderWrites
            opts.AllowRawFolderWrites = gscodeSection["allowRawFolderWrites"]?.Value<bool>() ?? false;

            // workspaceIndexingMode
            var indexingModeStr = gscodeSection["workspaceIndexingMode"]?.Value<string>()?.ToLowerInvariant();
            opts.WorkspaceIndexingMode = indexingModeStr switch
            {
                "full"    => IndexingMode.Full,
                "partial" => IndexingMode.Partial,
                _         => IndexingMode.Off
            };

            // serverLogLevel
            var logLevelStr = gscodeSection["serverLogLevel"]?.Value<string>()?.ToLowerInvariant();
            loggingLevelSwitch.MinimumLevel = logLevelStr switch
            {
                "messages" => LogEventLevel.Information,
                "verbose"  => LogEventLevel.Debug,
                _          => LogEventLevel.Warning   // "off" or unset
            };

            // enableWorkspaceCache
            scriptManager.UseWorkspaceCache = gscodeSection["enableWorkspaceCache"]?.Value<bool>() ?? false;

            Log.Information("Configuration updated: IndexingMode={IndexingMode}, AllowRawFolderWrites={AllowWrites}, CustomRawPath={CustomRawPath}, EnableWorkspaceCache={EnableCache}, LogLevel={LogLevel}",
                opts.WorkspaceIndexingMode, opts.AllowRawFolderWrites, opts.CustomRawPath ?? "(none)", scriptManager.UseWorkspaceCache, loggingLevelSwitch.MinimumLevel);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to process configuration change");
        }
        return Unit.Task;
    }
}
