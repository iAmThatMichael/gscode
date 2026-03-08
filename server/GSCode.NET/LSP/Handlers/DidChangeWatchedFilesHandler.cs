using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace GSCode.NET.LSP.Handlers;

public class DidChangeWatchedFilesHandler : DidChangeWatchedFilesHandlerBase
{
    private readonly ILogger<DidChangeWatchedFilesHandler> _logger;
    private readonly ScriptManager _scriptManager;

    public DidChangeWatchedFilesHandler(
        ILogger<DidChangeWatchedFilesHandler> logger,
        ScriptManager scriptManager)
    {
        _logger = logger;
        _scriptManager = scriptManager;
    }

    public override Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Received workspace file change notification with {Count} changes", request.Changes.Count());

        foreach (var change in request.Changes)
        {
            var fileUri = change.Uri;
            var changeType = change.Type;

            _logger.LogDebug("File change: {Uri} ({ChangeType})", fileUri, changeType);

            // Filter for GSC/CSC/GSH files only
            var path = fileUri.GetFileSystemPath();
            if (!IsScriptFile(path))
            {
                continue;
            }

            switch (changeType)
            {
                case FileChangeType.Created:
                    _logger.LogInformation("Script file created externally: {Path}", path);
                    // If workspace indexing is enabled, the file will be picked up on next scan
                    break;

                case FileChangeType.Changed:
                    _logger.LogInformation("Script file modified externally: {Path}", path);
                    // File content changed outside the editor
                    // The editor documents take precedence over file system
                    break;

                case FileChangeType.Deleted:
                    _logger.LogInformation("Script file deleted externally: {Path}", path);
                    // Remove from any caches if needed
                    break;
            }
        }

        return Unit.Task;
    }

    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(DidChangeWatchedFilesCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/*.gsc",
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                },
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/*.csc",
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                },
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/*.gsh",
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                }
            )
        };
    }

    private static bool IsScriptFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".gsc" || extension == ".csc" || extension == ".gsh";
    }
}
