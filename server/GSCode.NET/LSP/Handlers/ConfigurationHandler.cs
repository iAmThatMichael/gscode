using GSCode.Parser.Configuration;
using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;

namespace GSCode.NET.LSP.Handlers;

internal class ConfigurationHandler : DidChangeConfigurationHandlerBase
{
    public override Task<Unit> Handle(DidChangeConfigurationParams request, CancellationToken cancellationToken)
    {
        try
        {
            var settings = request.Settings as JToken;
            var gscodeSection = settings?["gscode"];
            if (gscodeSection is null) return Unit.Task;

            var opts = CompletionOptions.Current;

            var customRawPath = gscodeSection["customRawPath"]?.Value<string>();
            opts.CustomRawPath = customRawPath;

            var allowWrites = gscodeSection["allowRawFolderWrites"]?.Value<bool>() ?? false;
            opts.AllowRawFolderWrites = allowWrites;

            var indexingModeStr = gscodeSection["workspaceIndexingMode"]?.Value<string>();
            if (!string.IsNullOrEmpty(indexingModeStr))
            {
                opts.WorkspaceIndexingMode = indexingModeStr.ToLowerInvariant() switch
                {
                    "full"    => IndexingMode.Full,
                    "partial" => IndexingMode.Partial,
                    _         => IndexingMode.Off
                };
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to process configuration change");
        }
        return Unit.Task;
    }
}
