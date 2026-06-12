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

    public override async Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        bool hasScriptChange = false;
        foreach (var change in request.Changes)
        {
            var path = change.Uri.ToUri().LocalPath;
            if (!IsScriptFile(path))
            {
                continue;
            }

            hasScriptChange = true;
            Log.Information("Script file {ChangeType}: {Path}", change.Type, path);

            // Drop per-file caches (lexed #insert tokens, macro definitions) before
            // re-parsing, so scripts that #insert the changed file see its new contents
            // rather than replaying stale cache entries. (#66)
            Script.InvalidateCachedFile(path);
        }

        if (hasScriptChange)
        {
            Log.Information("Watched script file changed; re-parsing all open editors");
            await scriptManager.ReparseAllOpenEditorsAsync(cancellationToken);
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
