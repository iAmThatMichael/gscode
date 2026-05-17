using GSCode.Data;
using GSCode.Parser;
using GSCode.Parser.Util;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using GSCode.Parser.Configuration;
using Serilog;
using System.Diagnostics;
using System.IO;

namespace GSCode.NET.LSP.Handlers;

internal class TextDocumentSyncHandler(
    ILanguageServerFacade facade,
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector)
    : TextDocumentSyncHandlerBase
{

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        ScriptLanguage language = ScriptLanguageExtensions.FromExtension(
            Path.GetExtension(uri.ToUri().LocalPath));
        return new(uri, language.ToLanguageId());
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = documentSelector,
            Change = TextDocumentSyncKind.Incremental,
            Save = new SaveOptions { IncludeText = false }
        };

    public TextDocumentChangeRegistrationOptions GetRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = documentSelector,
            SyncKind = TextDocumentSyncKind.Incremental
        };

    public override async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken ct)
    {
        Log.Information("Document opened: {Uri}", request.TextDocument.Uri);
        var sw = Stopwatch.StartNew();
        var diags = await scriptManager.AddEditorAsync(request.TextDocument, ct);
        facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = request.TextDocument.Uri,
            Diagnostics = new Container<Diagnostic>(diags)
        });
        sw.Stop();
        Log.Information("Document open processed in {ElapsedMs} ms with {DiagCount} diagnostics",
            sw.ElapsedMilliseconds, diags.Count());
        return Unit.Value;
    }

    public override async Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken ct)
    {
        var changeType = request.ContentChanges.Any(c => c.Range == null) ? "Full" : "Incremental";
        Log.Information("Document changed ({ChangeType}, {ChangeCount} change(s))",
            changeType, request.ContentChanges.Count());
        var sw = Stopwatch.StartNew();
        var diags = await scriptManager.UpdateEditorAsync(request.TextDocument, request.ContentChanges, ct);
        facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = request.TextDocument.Uri,
            Diagnostics = new Container<Diagnostic>(diags)
        });
        sw.Stop();
        Log.Information("Document change processed in {ElapsedMs} ms with {DiagCount} diagnostics",
            sw.ElapsedMilliseconds, diags.Count());
        return Unit.Value;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken ct)
    {
        Log.Information("Document closed");
        var sw = Stopwatch.StartNew();
        scriptManager.RemoveEditor(request.TextDocument);
        sw.Stop();
        Log.Information("Document close processed in {ElapsedMs} ms", sw.ElapsedMilliseconds);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken ct)
    {
        // Legacy escape hatch: allowRawFolderWrites=true behaves like warning mode "off".
        var warningMode = CompletionConfiguration.RawFileWarningMode;
        if (warningMode == RawFileWarningMode.Off || CompletionConfiguration.AllowRawFolderWrites)
        {
            return Unit.Task;
        }

        string path = request.TextDocument.Uri.ToUri().LocalPath;
        string? rawRoot = GetContainingRawRoot(path);
        if (rawRoot is null)
        {
            return Unit.Task;
        }

        // In Stock mode, only scripts that shipped with the mod tools warrant a warning —
        // user-owned shared scripts kept inside the raw folder stay quiet.
        if (warningMode == RawFileWarningMode.Stock)
        {
            string relativePath = Path.GetRelativePath(rawRoot, Path.GetFullPath(path));
            if (!StockScripts.IsStockScript(relativePath))
            {
                return Unit.Task;
            }
        }

        Log.Warning("Stock/raw file saved in protected raw folder: {Path}", path);
        facade.SendNotification("gscode/rawFolderWriteWarning", new { path });

        // Keep the on-disk cache warm so a crash-restart doesn't force a full re-parse
        // of every file touched during the session.
        _ = scriptManager.SaveWorkspaceCacheAsync();

        // If the saved file is used as an #insert source, evict its stale token cache
        // and re-parse all open editors that splice it in.
        string savedPath = request.TextDocument.Uri.ToUri().LocalPath;
        _ = scriptManager.NotifyInsertFileSavedAsync(savedPath, ct);

        return Unit.Task;
    }

    /// <summary>
    /// Returns the protected raw root folder that contains <paramref name="filePath"/>,
    /// or null when the file is outside all known raw roots. Roots checked: the configured
    /// custom raw path, then TA_GAME_PATH/share/raw and TA_TOOLS_PATH/share/raw.
    /// </summary>
    private static string? GetContainingRawRoot(string filePath)
    {
        try
        {
            string norm = Path.GetFullPath(filePath).Replace('/', '\\').ToLowerInvariant();

            string? custom = CompletionConfiguration.CustomRawPath;
            if (!string.IsNullOrEmpty(custom))
            {
                string nc = Path.GetFullPath(custom).Replace('/', '\\').ToLowerInvariant();
                if (norm.StartsWith(nc)) return Path.GetFullPath(custom);
            }

            foreach (string envVar in new[] { "TA_GAME_PATH", "TA_TOOLS_PATH" })
            {
                string? basePath = Environment.GetEnvironmentVariable(envVar);
                if (string.IsNullOrEmpty(basePath)) continue;

                string shareRaw = Path.Combine(basePath, "share", "raw");
                if (!Directory.Exists(shareRaw)) continue;

                string ns = Path.GetFullPath(shareRaw).Replace('/', '\\').ToLowerInvariant();
                if (norm.StartsWith(ns)) return Path.GetFullPath(shareRaw);
            }
        }
        catch { }
        return null;
    }

    }
