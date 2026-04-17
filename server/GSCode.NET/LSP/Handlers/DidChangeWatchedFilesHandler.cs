using GSCode.Parser;
using GSCode.Parser.Util;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;
using System.IO;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

internal class DidChangeWatchedFilesHandler(
    ScriptManager scriptManager) : DidChangeWatchedFilesHandlerBase
{
    private readonly ScriptManager _scriptManager = scriptManager;

    public override async Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        foreach (var change in request.Changes)
        {
            var path = change.Uri.ToUri().LocalPath;
            if (IsScriptFile(path))
            {
                switch (change.Type)
                {
                    case FileChangeType.Created: Log.Information("Script file created: {Path}", path); break;
                    case FileChangeType.Changed: Log.Information("Script file modified externally: {Path}", path); break;
                    case FileChangeType.Deleted: Log.Information("Script file deleted: {Path}", path); break;
                }
            }
        }

        bool hasScriptChange = request.Changes.Any(c => IsScriptFile(c.Uri.ToUri().LocalPath));
        if (hasScriptChange)
        {
            Log.Information("Watched script file changed; re-parsing all open editors");
            await _scriptManager.ReparseAllOpenEditorsAsync(cancellationToken);
        }

        return Unit.Value;
    }

    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(
        DidChangeWatchedFilesCapability capability, ClientCapabilities clientCapabilities)
        => new();

    private static bool IsScriptFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".gsc" or ".csc" or ".gsh";
    }
}
