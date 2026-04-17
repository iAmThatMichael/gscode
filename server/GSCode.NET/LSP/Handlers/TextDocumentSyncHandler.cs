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
    private readonly ILanguageServerFacade _facade = facade;
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
        => new(uri, "gsc");

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _documentSelector,
            Change = TextDocumentSyncKind.Incremental,
            Save = new SaveOptions { IncludeText = false }
        };

    public TextDocumentChangeRegistrationOptions GetRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _documentSelector,
            SyncKind = TextDocumentSyncKind.Incremental
        };

    public override async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken ct)
    {
        Log.Information("Document opened");
        var sw = Stopwatch.StartNew();
        var diags = await _scriptManager.AddEditorAsync(request.TextDocument, ct);
        _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
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
        var diags = await _scriptManager.UpdateEditorAsync(request.TextDocument, request.ContentChanges, ct);
        _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
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
        _scriptManager.RemoveEditor(request.TextDocument);
        sw.Stop();
        Log.Information("Document close processed in {ElapsedMs} ms", sw.ElapsedMilliseconds);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken ct)
    {
        if (!CompletionConfiguration.AllowRawFolderWrites)
        {
            string path = request.TextDocument.Uri.ToUri().LocalPath;
            if (IsInProtectedRawFolder(path))
            {
                Log.Warning("File saved in protected raw folder: {Path}", path);
                _facade.SendNotification("gscode/rawFolderWriteWarning", new { path });
            }
        }
        return Unit.Task;
    }

    private static bool IsInProtectedRawFolder(string filePath)
    {
        try
        {
            string norm = Path.GetFullPath(filePath).Replace('/', '\\').ToLowerInvariant();

            string? custom = CompletionConfiguration.CustomRawPath;
            if (!string.IsNullOrEmpty(custom))
            {
                string nc = Path.GetFullPath(custom).Replace('/', '\\').ToLowerInvariant();
                if (norm.StartsWith(nc)) return true;
            }

            string? taGame = Environment.GetEnvironmentVariable("TA_GAME_PATH");
            if (!string.IsNullOrEmpty(taGame))
            {
                string shareRaw = Path.Combine(taGame, "share", "raw");
                if (Directory.Exists(shareRaw))
                {
                    string ns = Path.GetFullPath(shareRaw).Replace('/', '\\').ToLowerInvariant();
                    if (norm.StartsWith(ns)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    }
