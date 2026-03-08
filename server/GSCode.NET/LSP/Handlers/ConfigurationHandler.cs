using GSCode.Parser.Configuration;
using MediatR;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using System.Threading;
using System.Threading.Tasks;

namespace GSCode.NET.LSP.Handlers;

internal class ConfigurationHandler : DidChangeConfigurationHandlerBase
{
    private readonly ILogger<ConfigurationHandler> _logger;

    public ConfigurationHandler(ILogger<ConfigurationHandler> logger)
    {
        _logger = logger;
    }

    public override Task<Unit> Handle(DidChangeConfigurationParams request, CancellationToken cancellationToken)
    {
        try
        {
            // Parse the configuration settings
            if (request.Settings is JObject settings)
            {
                var gscodeSection = settings["gscode"];
                if (gscodeSection != null)
                {
                    // Read customRawPath setting
                    var customRawPath = gscodeSection["customRawPath"]?.Value<string>();
                    if (customRawPath != null)
                    {
                        _logger.LogInformation("Configuration: Setting customRawPath to {Path}", customRawPath);
                        CompletionConfiguration.CustomRawPath = customRawPath;
                    }
                    else
                    {
                        _logger.LogInformation("Configuration: customRawPath not set or null");
                        CompletionConfiguration.CustomRawPath = null;
                    }

                    // Read allowRawFolderWrites setting
                    var allowRawFolderWrites = gscodeSection["allowRawFolderWrites"]?.Value<bool>();
                    if (allowRawFolderWrites.HasValue)
                    {
                        _logger.LogInformation("Configuration: Setting allowRawFolderWrites to {Value}", allowRawFolderWrites.Value);
                        CompletionConfiguration.AllowRawFolderWrites = allowRawFolderWrites.Value;
                    }
                    else
                    {
                        _logger.LogInformation("Configuration: allowRawFolderWrites not set, using default (false)");
                        CompletionConfiguration.AllowRawFolderWrites = false;
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to process configuration change");
        }

        return Unit.Task;
    }
}
