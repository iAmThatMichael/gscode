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

    public override async Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        bool needsEditorReparse = false;

        foreach (var change in request.Changes)
        {
            var path = change.Uri.GetFileSystemPath();
            if (!IsScriptFile(path))
            {
                continue;
            }

            switch (change.Type)
            {
                case FileChangeType.Created:
                    // A new file appeared — it may satisfy a previously-unresolvable #using
                    // in one or more open editor scripts. Because the file was missing at
                    // parse time it was never tracked as a dependency, so we have no targeted
                    // dependents list to invalidate. Re-parse all open editors instead.
                    _logger.LogInformation("Script file created: {Path}", path);
                    needsEditorReparse = true;
                    break;

                case FileChangeType.Changed:
                    // Only act on files that are NOT open in the editor; those are handled
                    // by the DidChange notification. For external dependency changes we
                    // re-parse open editors so they pick up the updated exports.
                    _logger.LogInformation("Script file modified externally: {Path}", path);
                    needsEditorReparse = true;
                    break;

                case FileChangeType.Deleted:
                    // A dependency was removed — any open editor that #uses it should now
                    // receive a MissingUsingFile diagnostic.
                    _logger.LogInformation("Script file deleted: {Path}", path);
                    needsEditorReparse = true;
                    break;
            }
        }

        if (needsEditorReparse)
        {
            await _scriptManager.ReparseAllOpenEditorsAsync(cancellationToken);
        }

        return Unit.Value;
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
