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
    private readonly CompletionOptions _options;

    public ConfigurationHandler(ILogger<ConfigurationHandler> logger, CompletionOptions options)
    {
        _logger = logger;
        _options = options;
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
                        _options.CustomRawPath = customRawPath;
                    }
                    else
                    {
                        _logger.LogInformation("Configuration: customRawPath not set or null");
                        _options.CustomRawPath = null;
                    }

                    // Read allowRawFolderWrites setting
                    var allowRawFolderWrites = gscodeSection["allowRawFolderWrites"]?.Value<bool>();
                    if (allowRawFolderWrites.HasValue)
                    {
                        _logger.LogInformation("Configuration: Setting allowRawFolderWrites to {Value}", allowRawFolderWrites.Value);
                        _options.AllowRawFolderWrites = allowRawFolderWrites.Value;
                    }
                    else
                    {
                        _logger.LogInformation("Configuration: allowRawFolderWrites not set, using default (false)");
                        _options.AllowRawFolderWrites = false;
                    }

                    // Read workspaceIndexingMode setting (consolidated from enableWorkspaceIndexing)
                    var indexingMode = gscodeSection["workspaceIndexingMode"]?.Value<string>();
                    if (!string.IsNullOrEmpty(indexingMode))
                    {
                        if (indexingMode.Equals("off", System.StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("Configuration: Setting workspaceIndexingMode to Off");
                            _options.WorkspaceIndexingMode = IndexingMode.Off;
                        }
                        else if (indexingMode.Equals("full", System.StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("Configuration: Setting workspaceIndexingMode to Full");
                            _options.WorkspaceIndexingMode = IndexingMode.Full;
                        }
                        else if (indexingMode.Equals("partial", System.StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("Configuration: Setting workspaceIndexingMode to Partial");
                            _options.WorkspaceIndexingMode = IndexingMode.Partial;
                        }
                        else
                        {
                            _logger.LogWarning("Configuration: Invalid workspaceIndexingMode '{Mode}', using default (Off)", indexingMode);
                            _options.WorkspaceIndexingMode = IndexingMode.Off;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Configuration: workspaceIndexingMode not set, using default (Off)");
                        _options.WorkspaceIndexingMode = IndexingMode.Off;
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
