using Serilog;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using System.IO;

namespace GSCode.NET.LSP;

public sealed partial class GsCodeLanguageServer
{
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
                completionOpts.WorkspaceIndexingMode = indexingMode;
            }
            _scriptManager.CustomRawPath = customRawPath;

            Log.Information("Settings: LogLevel={LogLevel}, IndexingMode={IndexingMode}, AllowRawFolderWrites={AllowWrites}, CustomRawPath={CustomRawPath}",
                logLevel, indexingMode, allowWrites, customRawPath ?? "(none)");
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
                // DocumentHighlightProvider not yet implemented — don't advertise
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
}
