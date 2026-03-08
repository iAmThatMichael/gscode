using GSCode.Parser.Configuration;
using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using System.Collections.Immutable;
using System.Diagnostics;

namespace GSCode.NET.LSP.Handlers;

public class TextDocumentSyncHandler : ITextDocumentSyncHandler
{
    private readonly ILanguageServerFacade _facade;
    private readonly ScriptManager _scriptManager;
    private readonly ILogger<TextDocumentSyncHandler> _logger;
    private readonly TextDocumentSelector _documentSelector;

    public TextDocumentSyncHandler(ILanguageServerFacade facade, 
        ScriptManager scriptManager,
        ILogger<TextDocumentSyncHandler> logger,
        TextDocumentSelector documentSelector)
    {
        _facade = facade;
        _scriptManager = scriptManager;
        _logger = logger;
        _documentSelector = documentSelector;
    }

    public TextDocumentSyncKind Change { get; } = TextDocumentSyncKind.Incremental;

    public TextDocumentChangeRegistrationOptions GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new()
        {
            DocumentSelector = _documentSelector,
            SyncKind = Change
        };
    }

    public TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new(uri, "gsc");
    }

    public async Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var changeType = request.ContentChanges.Any(c => c.Range == null) ? "Full" : "Incremental";
        var changeCount = request.ContentChanges.Count();
        _logger.LogInformation("Document changed ({ChangeType}, {ChangeCount} change(s))", changeType, changeCount);

        var sw = Stopwatch.StartNew();
        var diagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();

        IEnumerable<Diagnostic> results = await _scriptManager.UpdateEditorAsync(request.TextDocument, request.ContentChanges, cancellationToken);

        foreach(Diagnostic result in results)
        {
            diagnostics.Add(result);
        }

        _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
        {
            Diagnostics = new Container<Diagnostic>(diagnostics.ToArray()),
            Uri = request.TextDocument.Uri,
            Version = request.TextDocument.Version
        });
        sw.Stop();
        _logger.LogInformation("Document change processed in {ElapsedMs} ms with {DiagCount} diagnostics", sw.ElapsedMilliseconds, diagnostics.Count);
        return Unit.Value;
    }

    public async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Document opened");
        var sw = Stopwatch.StartNew();
        IEnumerable<Diagnostic> resultingDiagnostics = await _scriptManager.AddEditorAsync(request.TextDocument, cancellationToken);

        _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
        {
            Diagnostics = resultingDiagnostics.ToArray(),
            Uri = request.TextDocument.Uri,
            Version = request.TextDocument.Version
        });
        sw.Stop();
        _logger.LogInformation("Document open processed in {ElapsedMs} ms with {DiagCount} diagnostics", sw.ElapsedMilliseconds, resultingDiagnostics.Count());
        return Unit.Value;
    }

    public Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Document closed");
        var sw = Stopwatch.StartNew();
        _scriptManager.RemoveEditor(request.TextDocument);
        sw.Stop();
        _logger.LogInformation("Document close processed in {ElapsedMs} ms", sw.ElapsedMilliseconds);
        return Unit.Task;
    }

    public Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        // Check if write protection is enabled
        if (!CompletionConfiguration.AllowRawFolderWrites)
        {
            string filePath = GetLocalPath(request.TextDocument.Uri);
            if (IsInProtectedRawFolder(filePath))
            {
                _logger.LogWarning("File saved in protected raw folder: {Path}. Consider setting gscode.allowRawFolderWrites to false or working in a separate mod directory.", filePath);

                // Send window/showMessage notification to the client
                _facade.SendNotification("window/showMessage", new ShowMessageParams
                {
                    Type = MessageType.Error,
                    Message = "You are editing a file in a protected raw folder. Consider working in a separate mod directory to avoid modifying vanilla game files."
                });
            }
        }
        return Unit.Task;
    }

    private string GetLocalPath(DocumentUri uri)
    {
        try
        {
            if (Uri.TryCreate(uri.ToString(), UriKind.Absolute, out Uri? parsedUri) && parsedUri.IsFile)
            {
                return parsedUri.LocalPath;
            }
            else if (uri.ToString().StartsWith("/") && uri.ToString().Length > 2 && uri.ToString()[2] == ':')
            {
                // Handle URI-style path like "/g:/..." -> "G:\..."
                return uri.ToString().Substring(1).Replace('/', '\\');
            }
            return uri.ToString();
        }
        catch
        {
            return uri.ToString();
        }
    }

    private bool IsInProtectedRawFolder(string filePath)
    {
        try
        {
            // Normalize path for comparison
            string normalizedPath = Path.GetFullPath(filePath).Replace('/', '\\').ToLowerInvariant();

            // Check if file is in custom raw path
            string? customRawPath = CompletionConfiguration.CustomRawPath;
            if (!string.IsNullOrEmpty(customRawPath))
            {
                string normalizedCustomPath = Path.GetFullPath(customRawPath).Replace('/', '\\').ToLowerInvariant();
                if (normalizedPath.StartsWith(normalizedCustomPath))
                {
                    _logger.LogDebug("File is in custom raw folder: {Path}", filePath);
                    return true;
                }
            }

            // Check if file is in TA_GAME_PATH\share\raw
            string? taGamePath = Environment.GetEnvironmentVariable("TA_GAME_PATH");
            if (!string.IsNullOrEmpty(taGamePath) && Directory.Exists(taGamePath))
            {
                string shareRawPath = Path.Combine(taGamePath, "share", "raw");
                if (Directory.Exists(shareRawPath))
                {
                    string normalizedShareRawPath = Path.GetFullPath(shareRawPath).Replace('/', '\\').ToLowerInvariant();
                    if (normalizedPath.StartsWith(normalizedShareRawPath))
                    {
                        _logger.LogDebug("File is in default raw folder (TA_GAME_PATH\\share\\raw): {Path}", filePath);
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if file is in protected raw folder: {Path}", filePath);
            return false;
        }
    }

    TextDocumentOpenRegistrationOptions IRegistration<TextDocumentOpenRegistrationOptions, TextSynchronizationCapability>.GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new()
        {
            DocumentSelector = _documentSelector
        };
    }

    TextDocumentCloseRegistrationOptions IRegistration<TextDocumentCloseRegistrationOptions, TextSynchronizationCapability>.GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new()
        {
            DocumentSelector = _documentSelector
        };
    }

    TextDocumentSaveRegistrationOptions IRegistration<TextDocumentSaveRegistrationOptions, TextSynchronizationCapability>.GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new()
        {
            DocumentSelector = _documentSelector
        };
    }
}
