using Serilog;
using GSCode.Parser;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using System.IO;
using System.Linq;

namespace GSCode.NET.LSP;

/// <summary>
/// The GSCode language server.  Wires <see cref="StreamJsonRpc.JsonRpc"/> to
/// <see cref="ScriptManager"/> and implements every LSP method as a
/// <c>[JsonRpcMethod]</c> handler.  Replaces the former OmniSharp server builder.
/// </summary>
public sealed class GsCodeLanguageServer : ILspNotifier, IDisposable
{
    private readonly JsonRpc _rpc;
    private readonly ScriptManager _scriptManager;
    private readonly SemanticTokensLegend _semanticTokensLegend;

    // Workspace folders captured during Initialize for use in Initialized
    private string[] _workspaceFolders = [];
    private bool _shuttingDown;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public GsCodeLanguageServer(Stream input, Stream output)
    {
        // ScriptManager is injected with 'this' as the ILspNotifier so it can
        // push diagnostics back through the same JsonRpc channel.
        _scriptManager = new ScriptManager(this);

        _semanticTokensLegend = BuildLegend();

        var formatter = new JsonMessageFormatter();
        // The MS LSP SDK types use Newtonsoft.Json [JsonConverter] attributes for
        // union types (SumType, etc.).  Ensure enum serialisation matches the LSP
        // spec which sends string values, not integers.
        formatter.JsonSerializer.Converters.Add(
            new Newtonsoft.Json.Converters.StringEnumConverter());

        HeaderDelimitedMessageHandler handler;
        if (ReferenceEquals(input, output))
        {
            // Single bidirectional stream (e.g. named pipe)
            handler = new HeaderDelimitedMessageHandler(input, formatter);
        }
        else
        {
            // Separate streams (e.g. stdio: output = send, input = receive)
            handler = new HeaderDelimitedMessageHandler(output, input, formatter);
        }

        _rpc = new JsonRpc(handler, this);
        _rpc.ExceptionStrategy = ExceptionProcessing.CommonErrorData;
        _rpc.Disconnected += OnDisconnected;

        // Enable verbose tracing to see incoming/outgoing messages
        _rpc.TraceSource = new System.Diagnostics.TraceSource("LSP", System.Diagnostics.SourceLevels.Verbose);
        _rpc.TraceSource.Listeners.Add(new SerilogTraceListener());
    }

    private void OnDisconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        Log.Warning("JsonRpc disconnected: {Reason} | LastMessage={LastMessage} | Exception={Exception}",
            e.Reason, e.LastMessage, e.Exception?.Message);
    }

    public void Start() => _rpc.StartListening();
    public Task WaitForExitAsync() => _rpc.Completion;

    public void Dispose() => _rpc.Dispose();

    // -------------------------------------------------------------------------
    // ILspNotifier
    // -------------------------------------------------------------------------

    public Task PublishDiagnosticsAsync(Uri uri, IEnumerable<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return _rpc.NotifyAsync(Methods.TextDocumentPublishDiagnosticsName,
                new PublishDiagnosticParams
                {
                    Uri = uri,
                    Diagnostics = diagnostics.ToArray()
                });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to publish diagnostics for {Uri}", uri.LocalPath);
            return Task.CompletedTask;
        }
    }

    // -------------------------------------------------------------------------
    // LSP lifecycle
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.InitializeName, UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeParams @params)
    {
        Log.Information(">>> Initialize received! RootUri={RootUri}", @params.RootUri);
        // Parse initialization options (may be null when client sends none)
        JToken? opts = @params.InitializationOptions as JToken;
        if (opts is not null)
        {
            var logLevel = InitializationOptionsReader.ParseServerLogLevel(opts);
            var indexingMode = InitializationOptionsReader.ParseWorkspaceIndexingMode(opts);
            var customRawPath = InitializationOptionsReader.ParseCustomRawPath(opts);
            var allowWrites = InitializationOptionsReader.ParseAllowRawFolderWrites(opts);

            // Apply options to configuration
            var completionOpts = GSCode.Parser.Configuration.CompletionOptions.Current;
            if (completionOpts is not null)
            {
                completionOpts.CustomRawPath = customRawPath;
                completionOpts.AllowRawFolderWrites = allowWrites;
            }
            _scriptManager.CustomRawPath = customRawPath;
        }

        // Collect workspace folders for use in Initialized
        if (@params.RootUri is not null)
            _workspaceFolders = [UriHelper.GetLocalPath(@params.RootUri)];

        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Incremental,
                    Save = new SaveOptions { IncludeText = false }
                },
                HoverProvider = true,
                CompletionProvider = new CompletionOptions
                {
                    ResolveProvider = true,
                    TriggerCharacters = [".", ":", "#", "(", ",", "\\", "/"]
                },
                SignatureHelpProvider = new SignatureHelpOptions
                {
                    TriggerCharacters = ["(", ",", ")"],
                    RetriggerCharacters = [",", ")"]
                },
                DefinitionProvider = true,
                ReferencesProvider = true,
                DocumentHighlightProvider = true,
                DocumentSymbolProvider = true,
                CodeActionProvider = new CodeActionOptions { ResolveProvider = true },
                FoldingRangeProvider = true,
                SemanticTokensOptions = new SemanticTokensOptions
                {
                    Legend = _semanticTokensLegend,
                    Full = true,
                    Range = false
                }
            }
        };
    }

    [JsonRpcMethod(Methods.InitializedName, UseSingleObjectParameterDeserialization = true)]
    public void Initialized(InitializedParams @params)
    {
        Log.Information(">>> Initialized received. Workspace folders: [{Folders}]",
            string.Join(", ", _workspaceFolders));

        // Start workspace indexing on a background thread per folder
        if (_workspaceFolders.Length > 0)
        {
            var cts = new CancellationTokenSource();
            foreach (var folder in _workspaceFolders)
            {
                string root = folder;
                if (Directory.Exists(root))
                {
                    Log.Information("Starting workspace indexing: {Root}", root);
                    _ = Task.Run(() => _scriptManager.IndexWorkspaceAsync(root, cts.Token), cts.Token);
                }
                else
                {
                    Log.Warning("Workspace folder does not exist: {Root}", root);
                }
            }
        }
    }

    [JsonRpcMethod(Methods.ShutdownName)]
    public Task ShutdownAsync()
    {
        _shuttingDown = true;
        return Task.CompletedTask;
    }

    [JsonRpcMethod(Methods.ExitName)]
    public void Exit() => Environment.Exit(_shuttingDown ? 0 : 1);

    // -------------------------------------------------------------------------
    // textDocument/did*
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentDidOpenName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidOpenAsync(DidOpenTextDocumentParams @params, CancellationToken ct)
    {
        var diags = await _scriptManager.AddEditorAsync(@params.TextDocument, ct);
        await PublishDiagnosticsAsync(@params.TextDocument.Uri, diags, ct);
    }

    [JsonRpcMethod(Methods.TextDocumentDidChangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidChangeAsync(DidChangeTextDocumentParams @params, CancellationToken ct)
    {
        var versionedId = new VersionedTextDocumentIdentifier
        {
            Uri = @params.TextDocument.Uri,
            Version = @params.TextDocument.Version
        };
        var diags = await _scriptManager.UpdateEditorAsync(versionedId, @params.ContentChanges, ct);
        await PublishDiagnosticsAsync(@params.TextDocument.Uri, diags, ct);
    }

    [JsonRpcMethod(Methods.TextDocumentDidCloseName, UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams @params)
        => _scriptManager.RemoveEditor(@params.TextDocument);

    [JsonRpcMethod(Methods.TextDocumentDidSaveName, UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams @params)
    {
        if (!GSCode.Parser.Configuration.CompletionConfiguration.AllowRawFolderWrites)
        {
            string path = UriHelper.GetLocalPath(@params.TextDocument.Uri);
            if (IsInProtectedRawFolder(path))
            {
                _ = _rpc.NotifyAsync(Methods.WindowShowMessageName, new ShowMessageParams
                {
                    MessageType = MessageType.Error,
                    Message = "You are editing a file in a protected raw folder. Consider working in a separate mod directory to avoid modifying vanilla game files."
                });
            }
        }
    }

    // -------------------------------------------------------------------------
    // textDocument/hover
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentHoverName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return null;
        return await script.GetHoverAsync(@params.Position, ct);
    }

    // -------------------------------------------------------------------------
    // textDocument/completion + completionItem/resolve
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentCompletionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionList> CompletionAsync(CompletionParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return new CompletionList { IsIncomplete = false, Items = [] };
        return await script.GetCompletionAsync(@params.Position, ct)
            ?? new CompletionList { IsIncomplete = false, Items = [] };
    }

    [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
    public Task<CompletionItem> ResolveCompletionItemAsync(CompletionItem item, CancellationToken ct)
        => Task.FromResult(item);

    // -------------------------------------------------------------------------
    // textDocument/semanticTokens/full
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentSemanticTokensFullName, UseSingleObjectParameterDeserialization = true)]
    public async Task<SemanticTokens?> SemanticTokensFullAsync(SemanticTokensParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return null;
        var tokens = await script.GetSemanticTokensAsync(ct);
        return EncodeSemanticTokens(tokens, _semanticTokensLegend);
    }

    // -------------------------------------------------------------------------
    // textDocument/definition
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentDefinitionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Location?> DefinitionAsync(TextDocumentPositionParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return null;
        return await script.GetDefinitionAsync(@params.Position, ct);
    }

    // -------------------------------------------------------------------------
    // textDocument/references
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentReferencesName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Location[]> ReferencesAsync(ReferenceParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return [];

        // Try local variable references first
        var localRefs = await script.GetLocalVariableReferencesAsync(
            @params.Position, @params.Context?.IncludeDeclaration == true, ct);
        if (localRefs.Count > 0)
            return localRefs.Select(r => new Location { Uri = @params.TextDocument.Uri, Range = r }).ToArray();

        var qid = await script.GetQualifiedIdentifierAtAsync(@params.Position, ct);
        if (qid is null) return [];

        string ns = qid.Value.qualifier ?? (script.DefinitionsTable?.CurrentNamespace ?? "");
        string name = qid.Value.name;

        var keys = new[]
        {
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Function, ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Method,   ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Class,    ns, name)
        };

        var results = new List<Location>();
        foreach (var loaded in _scriptManager.GetLoadedScripts())
        {
            foreach (var key in keys)
            {
                if (loaded.Script.References.TryGetValue(key, out var ranges))
                    foreach (var r in ranges)
                        results.Add(new Location { Uri = loaded.Uri, Range = r });
            }

            if (@params.Context?.IncludeDeclaration == true && loaded.Script.DefinitionsTable is not null)
            {
                foreach (var key in keys)
                {
                    var loc = loaded.Script.DefinitionsTable.GetFunctionLocation(key.Namespace, key.Name)
                           ?? loaded.Script.DefinitionsTable.GetClassLocation(key.Namespace, key.Name);
                    if (loc is not null)
                    {
                        string norm = ScriptFileResolver.NormalizeFilePathForUri(loc.Value.FilePath);
                        results.Add(new Location { Uri = new Uri(norm), Range = loc.Value.Range.ToRange() });
                    }
                }
            }
        }

        return results.ToArray();
    }

    // -------------------------------------------------------------------------
    // textDocument/documentSymbol
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentDocumentSymbolName, UseSingleObjectParameterDeserialization = true)]
    public async Task<DocumentSymbol[]> DocumentSymbolAsync(DocumentSymbolParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null || script.DefinitionsTable is null) return [];
        ct.ThrowIfCancellationRequested();

        string currentPath = ScriptFileResolver.NormalizeFilePathForUri(UriHelper.GetLocalPath(@params.TextDocument.Uri));

        static string BuildFunctionLabel(string name, string? ns, string[]? parameters, string[]? flags)
        {
            string paramStr = parameters is null ? "()" : $"({string.Join(", ", parameters)})";
            string flagStr = flags is { Length: > 0 } ? $" [{string.Join(", ", flags)}]" : "";
            return $"{name}{paramStr}{flagStr}";
        }

        var classNodes = new List<DocumentSymbol>();
        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            ct.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            classNodes.Add(new DocumentSymbol
            {
                Name = kv.Key.SymbolName, Detail = kv.Key.Qualifier,
                Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Class,
                Range = kv.Value.Range.ToRange(), SelectionRange = kv.Value.Range.ToRange(),
                Children = []
            });
        }

        var functionNodes = new List<DocumentSymbol>();
        foreach (var kv in script.DefinitionsTable.GetAllFunctionLocations())
        {
            ct.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            string[]? parameters = script.DefinitionsTable.GetFunctionParameters(kv.Key.Qualifier, kv.Key.SymbolName);
            string[]? flags = script.DefinitionsTable.GetFunctionFlags(kv.Key.Qualifier, kv.Key.SymbolName);
            functionNodes.Add(new DocumentSymbol
            {
                Name = BuildFunctionLabel(kv.Key.SymbolName, kv.Key.Qualifier, parameters, flags),
                Detail = kv.Key.Qualifier,
                Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Function,
                Range = kv.Value.Range.ToRange(), SelectionRange = kv.Value.Range.ToRange()
            });
        }

        var macroNodes = new List<DocumentSymbol>();
        foreach (var m in script.MacroOutlines)
        {
            ct.ThrowIfCancellationRequested();
            macroNodes.Add(new DocumentSymbol
            {
                Name = m.Name,
                Detail = string.IsNullOrEmpty(m.SourceDisplay) ? "#define" : m.SourceDisplay,
                Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Constant,
                Range = m.Range, SelectionRange = m.Range
            });
        }

        var root = new List<DocumentSymbol>(3);
        Range AnchorAt(int line) => new Range { Start = new Position { Line = line, Character = 0 }, End = new Position { Line = line, Character = 0 } };

        if (classNodes.Count > 0)
        {
            classNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Classes", Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Namespace, Range = AnchorAt(0), SelectionRange = AnchorAt(0), Children = classNodes.ToArray() });
        }
        if (functionNodes.Count > 0)
        {
            functionNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Functions", Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Namespace, Range = AnchorAt(1), SelectionRange = AnchorAt(1), Children = functionNodes.ToArray() });
        }
        if (macroNodes.Count > 0)
        {
            macroNodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            root.Add(new DocumentSymbol { Name = "Macros", Kind = Microsoft.VisualStudio.LanguageServer.Protocol.SymbolKind.Namespace, Range = AnchorAt(2), SelectionRange = AnchorAt(2), Children = macroNodes.ToArray() });
        }

        return root.ToArray();
    }

    // -------------------------------------------------------------------------
    // textDocument/signatureHelp
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentSignatureHelpName, UseSingleObjectParameterDeserialization = true)]
    public async Task<SignatureHelp?> SignatureHelpAsync(TextDocumentPositionParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return null;
        return await script.GetSignatureHelpAsync(@params.Position, ct);
    }

    // -------------------------------------------------------------------------
    // textDocument/foldingRange
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentFoldingRangeName, UseSingleObjectParameterDeserialization = true)]
    public async Task<FoldingRange[]> FoldingRangeAsync(FoldingRangeParams @params, CancellationToken ct)
    {
        Script? script = _scriptManager.GetParsedEditor(@params.TextDocument.Uri);
        if (script is null) return [];
        var ranges = await script.GetFoldingRangesAsync(ct);
        return ranges.ToArray();
    }

    // -------------------------------------------------------------------------
    // textDocument/documentHighlight
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentDocumentHighlightName, UseSingleObjectParameterDeserialization = true)]
    public Task<DocumentHighlight[]> DocumentHighlightAsync(TextDocumentPositionParams @params, CancellationToken ct)
    {
        // DocumentHighlight is not yet implemented in the parser layer.
        return Task.FromResult(Array.Empty<DocumentHighlight>());
    }

    // -------------------------------------------------------------------------
    // textDocument/codeAction + codeAction/resolve
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.TextDocumentCodeActionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CodeAction[]> CodeActionAsync(CodeActionParams @params, CancellationToken ct)
    {
        var handler = new Handlers.CodeActionHandler(_scriptManager);
        return await handler.GetCodeActionsAsync(@params, ct);
    }

    [JsonRpcMethod(Methods.CodeActionResolveName, UseSingleObjectParameterDeserialization = true)]
    public Task<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct)
        => Task.FromResult(action);

    // -------------------------------------------------------------------------
    // workspace/didChangeConfiguration
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.WorkspaceDidChangeConfigurationName, UseSingleObjectParameterDeserialization = true)]
    public void DidChangeConfiguration(DidChangeConfigurationParams @params)
    {
        try
        {
            var settings = @params.Settings as JToken;
            var gscodeSection = settings?["gscode"];
            if (gscodeSection is null) return;

            var indexingModeStr = gscodeSection["workspaceIndexingMode"]?.Value<string>();
            if (!string.IsNullOrEmpty(indexingModeStr))
            {
                var mode = indexingModeStr.ToLowerInvariant() switch
                {
                    "full"    => GSCode.Parser.Configuration.IndexingMode.Full,
                    "partial" => GSCode.Parser.Configuration.IndexingMode.Partial,
                    _         => GSCode.Parser.Configuration.IndexingMode.Off
                };
                var currentOpts = GSCode.Parser.Configuration.CompletionOptions.Current;
                if (currentOpts is not null) currentOpts.WorkspaceIndexingMode = mode;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to process configuration change");
        }
    }

    // -------------------------------------------------------------------------
    // workspace/didChangeWatchedFiles
    // -------------------------------------------------------------------------

    [JsonRpcMethod(Methods.WorkspaceDidChangeWatchedFilesName, UseSingleObjectParameterDeserialization = true)]
    public async Task DidChangeWatchedFilesAsync(DidChangeWatchedFilesParams @params, CancellationToken ct)
    {
        bool hasScriptChange = @params.Changes.Any(c => IsScriptFile(UriHelper.GetLocalPath(c.Uri)));
        if (hasScriptChange)
        {
            Log.Information("Watched script file changed; re-parsing all open editors");
            await _scriptManager.ReparseAllOpenEditorsAsync(ct);
        }
    }

    // -------------------------------------------------------------------------
    // Semantic token encoding
    // -------------------------------------------------------------------------

    private static SemanticTokensLegend BuildLegend() => new()
    {
        TokenTypes =
        [
            SemanticTokenTypes.Namespace, SemanticTokenTypes.Type,     SemanticTokenTypes.Class,
            SemanticTokenTypes.Enum,      SemanticTokenTypes.Interface, SemanticTokenTypes.Struct,
            SemanticTokenTypes.TypeParameter, SemanticTokenTypes.Parameter, SemanticTokenTypes.Variable,
            SemanticTokenTypes.Property,  SemanticTokenTypes.EnumMember, SemanticTokenTypes.Event,
            SemanticTokenTypes.Function,  SemanticTokenTypes.Method,   SemanticTokenTypes.Macro,
            SemanticTokenTypes.Keyword,   SemanticTokenTypes.Modifier, SemanticTokenTypes.Comment,
            SemanticTokenTypes.String,    SemanticTokenTypes.Number,   SemanticTokenTypes.Regexp,
            SemanticTokenTypes.Operator
        ],
        TokenModifiers = []
    };

    private static SemanticTokens EncodeSemanticTokens(
        IReadOnlyList<ISemanticToken> tokens,
        SemanticTokensLegend legend)
    {
        var typeIndex = legend.TokenTypes
            .Select((t, i) => (t, i))
            .ToDictionary(x => x.t, x => x.i, StringComparer.OrdinalIgnoreCase);

        var data = new List<int>(tokens.Count * 5);
        int prevLine = 0, prevChar = 0;

        foreach (var token in tokens)
        {
            if (!typeIndex.TryGetValue(token.SemanticTokenType, out int typeIdx)) continue;

            int line      = token.Range.Start.Line;
            int startChar = token.Range.Start.Character;
            int length    = token.Range.End.Character - token.Range.Start.Character;

            data.Add(line - prevLine);
            data.Add(line == prevLine ? startChar - prevChar : startChar);
            data.Add(length);
            data.Add(typeIdx);
            data.Add(0); // no token modifiers

            prevLine = line;
            prevChar = startChar;
        }

        return new SemanticTokens { Data = data.ToArray() };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool IsScriptFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".gsc" or ".csc" or ".gsh";
    }

    private static bool IsInProtectedRawFolder(string filePath)
    {
        try
        {
            string norm = Path.GetFullPath(filePath).Replace('/', '\\').ToLowerInvariant();

            string? custom = GSCode.Parser.Configuration.CompletionConfiguration.CustomRawPath;
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
        catch { /* ignore path errors */ }
        return false;
    }
}

/// <summary>
/// Routes <see cref="System.Diagnostics.TraceSource"/> output to Serilog
/// so JsonRpc wire-level tracing appears in the same log stream.
/// </summary>
file sealed class SerilogTraceListener : System.Diagnostics.TraceListener
{
    public override void Write(string? message) => Serilog.Log.Verbose("[LSP] {Message}", message);
    public override void WriteLine(string? message) => Serilog.Log.Verbose("[LSP] {Message}", message);
}


