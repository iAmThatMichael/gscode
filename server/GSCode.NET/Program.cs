using GSCode.NET;
using GSCode.NET.LSP;
using GSCode.NET.LSP.Handlers;
using GSCode.Parser.SPA;
using GSCode.Parser.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Diagnostics;

// Create a logging level switch that can be changed dynamically
var loggingLevelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

Log.Logger = new LoggerConfiguration()
				.MinimumLevel.ControlledBy(loggingLevelSwitch)
				.WriteTo.Console()
#if DEBUG
				.WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
#endif
				.CreateLogger();

Log.Information("GSCode Language Server");

// Determine the base directory of the executing assembly
string assemblyLocation = Assembly.GetExecutingAssembly().Location;
string? assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
string apiDirectory = assemblyDirectory is null ? "api" : Path.Combine(assemblyDirectory, "api");

string gscApiPath = Path.Combine(apiDirectory, "t7_api_gsc.json");
string cscApiPath = Path.Combine(apiDirectory, "t7_api_csc.json");

// Load GSC & CSC API into the SPA concurrently
await Task.WhenAll(
	ScriptAnalyserData.LoadLanguageApiAsync(
		"https://www.gscode.net/api/getLibrary?gameId=t7&languageId=gsc",
		gscApiPath
	),
	ScriptAnalyserData.LoadLanguageApiAsync(
		"https://www.gscode.net/api/getLibrary?gameId=t7&languageId=csc",
		cscApiPath
	)
);

ServerOptions serverOptions = new();
Parser.Default.ParseArguments<ServerOptions>(args).WithParsed(o => serverOptions = o);

Log.Information("Server args: {Args}", string.Join(" ", args));
Log.Information("Transport: pipe={Pipe} socket={Socket} stdio={Stdio}",
	serverOptions.Pipe, serverOptions.Socket, serverOptions.Stdio);

(Stream input, Stream output, IDisposable? disposable) = await StreamResolver.ResolveAsync(serverOptions, CancellationToken.None);

Log.Information("Transport stream resolved. Input={Input} Output={Output}", input.GetType().Name, output.GetType().Name);

LanguageServer server = await LanguageServer.From(options =>
{
	options
		.WithInput(input)
		.WithOutput(output)
		.WithConfigurationSection("gscode")
		.WithServices(services =>
		{
			services.AddSingleton<ScriptManager>(sp =>
			{
				var facade = sp.GetRequiredService<ILanguageServerFacade>();
				return new ScriptManager(new OmniSharpLspNotifier(facade));
			});
			services.AddSingleton(new TextDocumentSelector(
				new TextDocumentFilter { Pattern = "**/*.gsc" },
				new TextDocumentFilter { Pattern = "**/*.csc" },
				new TextDocumentFilter { Pattern = "**/*.gsh" }
			));
			services.AddSingleton(loggingLevelSwitch);
		})
		.OnInitialize(async (server, request, ct) =>
		{
			try
			{
				var sm = server.Services.GetRequiredService<ScriptManager>();

				if (request.InitializationOptions is JToken initOptions)
				{
					var indexingMode = InitializationOptionsReader.ParseWorkspaceIndexingMode(initOptions);
					var customPath   = InitializationOptionsReader.ParseCustomRawPath(initOptions);
					var allowWrites  = InitializationOptionsReader.ParseAllowRawFolderWrites(initOptions);
					var enableCache  = InitializationOptionsReader.ParseEnableWorkspaceCache(initOptions);
					var indexGame    = InitializationOptionsReader.ParseIndexGameScripts(initOptions);
					var warningMode  = InitializationOptionsReader.ParseRawFileWarningMode(initOptions);

					loggingLevelSwitch.MinimumLevel = InitializationOptionsReader.ParseServerLogLevel(initOptions);

					var completionOpts = CompletionOptions.Current;
					completionOpts.WorkspaceIndexingMode = indexingMode;
					completionOpts.AllowRawFolderWrites  = allowWrites;
					completionOpts.IndexGameScripts      = indexGame;
					completionOpts.RawFileWarningMode    = warningMode;

					if (customPath is not null)
					{
						completionOpts.CustomRawPath = customPath;
						sm.CustomRawPath = customPath;
						Log.Information("CustomRawPath set: {Path}", customPath);
					}

					sm.UseWorkspaceCache = enableCache;

					Log.Information("Settings: IndexingMode={IndexingMode}, AllowRawFolderWrites={AllowWrites}, CustomRawPath={CustomRawPath}, EnableWorkspaceCache={EnableCache}, IndexGameScripts={IndexGame}, RawFileWarningMode={WarningMode}",
						indexingMode, allowWrites, customPath ?? "(none)", enableCache, indexGame, warningMode);
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed during initialization");
			}
		})
		.OnInitialized(async (server, request, response, ct) =>
		{
			try
			{
				var indexingMode = CompletionConfiguration.WorkspaceIndexingMode;
				bool indexGameScripts = CompletionConfiguration.IndexGameScripts;

				if (indexingMode == IndexingMode.Off && !indexGameScripts)
				{
					Log.Information("Indexing is disabled (IndexingMode=Off, IndexGameScripts=false).");
					return;
				}

				var sm = server.Services.GetRequiredService<ScriptManager>();
				var indexingCts = new CancellationTokenSource();
				options.RegisterForDisposal(indexingCts);
				var indexingToken = indexingCts.Token;

				// Workspace folders: full/partial analysis per the configured mode, or a
				// signature-only pass when indexing is Off but game-script indexing is on
				// (so the registry still learns every namespace in the open map/mod).
				List<string> workspaceRoots = new();
				if (request.WorkspaceFolders is not null && request.WorkspaceFolders.Any())
				{
					workspaceRoots.AddRange(request.WorkspaceFolders
						.Select(wf => wf.Uri.ToUri().LocalPath)
						.Where(Directory.Exists));
				}
				else if (request.RootUri is not null && Directory.Exists(request.RootUri.ToUri().LocalPath))
				{
					workspaceRoots.Add(request.RootUri.ToUri().LocalPath);
				}

				// Game raw root: the custom raw path if configured, otherwise the
				// mod tools' share/raw. Indexed signature-only so every stock namespace is
				// known to completions and quick fixes without full-analysis cost.
				string? gameRawRoot = null;
				if (indexGameScripts)
				{
					if (!string.IsNullOrWhiteSpace(CompletionConfiguration.CustomRawPath))
					{
						gameRawRoot = CompletionConfiguration.CustomRawPath;
					}
					else
					{
						string? toolsPath = Environment.GetEnvironmentVariable("TA_TOOLS_PATH");
						if (!string.IsNullOrEmpty(toolsPath))
						{
							gameRawRoot = Path.Combine(toolsPath, "share", "raw");
						}
					}

					if (gameRawRoot is not null && !Directory.Exists(gameRawRoot))
					{
						Log.Warning("Game raw root does not exist, skipping game script indexing: {Path}", gameRawRoot);
						gameRawRoot = null;
					}
				}

				Log.Information("Starting indexing: mode={Mode}, workspaceRoots={Roots}, gameRawRoot={RawRoot}",
					indexingMode, string.Join(';', workspaceRoots), gameRawRoot ?? "(none)");

				// Run the roots sequentially in one background task — the workspace cache
				// file is shared, and parallel root indexing would race on load/save.
				_ = Task.Run(async () =>
				{
					foreach (string root in workspaceRoots)
					{
						bool workspaceSignatureOnly = indexingMode == IndexingMode.Off;

						// Avoid double-indexing when the user opened the raw folder itself.
						if (gameRawRoot is not null &&
							string.Equals(Path.GetFullPath(root), Path.GetFullPath(gameRawRoot), StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						Log.Information("Indexing workspace root ({Profile}): {Root}",
							workspaceSignatureOnly ? "signature-only" : indexingMode.ToString(), root);
						await sm.IndexWorkspaceAsync(root, signatureOnly: workspaceSignatureOnly, indexingToken);
					}

					if (gameRawRoot is not null)
					{
						Log.Information("Indexing game scripts (signature-only): {Root}", gameRawRoot);
						await sm.IndexWorkspaceAsync(gameRawRoot, signatureOnly: true, indexingToken);
					}
				}, indexingToken);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed to start workspace indexing");
			}
		})
		.AddHandler<TextDocumentSyncHandler>()
		.AddHandler<SemanticTokensHandler>()
		.AddHandler<HoverHandler>()
		.AddHandler<CompletionHandler>()
		.AddHandler<FoldingRangeHandler>()
		.AddHandler<DefinitionHandler>()
		.AddHandler<DocumentSymbolHandler>()
		.AddHandler<DocumentHighlightHandler>()
		.AddHandler<SignatureHelpHandler>()
		.AddHandler<ReferencesHandler>()
		.AddHandler<PrepareRenameHandler>()
		.AddHandler<RenameHandler>()
		.AddHandler<ConfigurationHandler>()
		.AddHandler<DidChangeWatchedFilesHandler>()
		.AddHandler<CodeActionHandler>()
		.AddHandler<WorkspaceSymbolHandler>()
		.AddHandler<CodeLensHandler>();

	if (disposable is not null)
		options.RegisterForDisposal(disposable);

}).ConfigureAwait(false);

Log.Information("Language server connected successfully!");

#if FLAG_MEMORY_DEBUG
// Memory monitoring
var memoryMonitorCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
	var process = Process.GetCurrentProcess();
	var scriptManager = server.Services.GetService<ScriptManager>();

	while (!memoryMonitorCts.Token.IsCancellationRequested)
	{
		try
		{
			process.Refresh();
			var memoryMB = process.WorkingSet64 / 1024.0 / 1024.0;
			var privateMemoryMB = process.PrivateMemorySize64 / 1024.0 / 1024.0;

			int gscCount = 0, cscCount = 0;

			int gscFunctions = 0, gscClasses = 0, cscFunctions = 0, cscClasses = 0;

			if (scriptManager != null)
			{
				(gscFunctions, gscClasses) = scriptManager.GetSymbolCounts(GSCode.Data.ScriptLanguage.Gsc);
				(cscFunctions, cscClasses) = scriptManager.GetSymbolCounts(GSCode.Data.ScriptLanguage.Csc);
				var scriptCounts = scriptManager.GetScriptCountsByType();
				gscCount = scriptCounts.GscFiles;
				cscCount = scriptCounts.CscFiles;
			}

			var macroStats = GSCode.Parser.Pre.MacroDefinitionCache.Instance.GetDetailedStatistics();

			// Only log GSH/Macro counts once indexing is complete — during the
			// parallel indexing phase these are mid-flight snapshots and will
			// vary by ±N depending on which files happened to finish first.
			string macroSuffix = (scriptManager?.IsIndexingComplete ?? false)
				? $"GSH={macroStats.GshFiles}, Macros: {macroStats.TotalMacros}"
				: $"GSH=<indexing>, Macros: <indexing>";

			Log.Information(
				"Memory Usage - Working Set: {WorkingSet:F2} MB, Private: {Private:F2} MB | " +
				"GSC: Functions={GscFunctions}, Classes={GscClasses}, Files={GscFiles} | " +
				"CSC: Functions={CscFunctions}, Classes={CscClasses}, Files={CscFiles} | " +
				"{MacroSuffix}",
				memoryMB, privateMemoryMB,
				gscFunctions, gscClasses, gscCount,
				cscFunctions, cscClasses, cscCount,
				macroSuffix);

			await Task.Delay(1000, memoryMonitorCts.Token);
		}
		catch (OperationCanceledException)
		{
			break;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error monitoring memory");
		}
	}
}, memoryMonitorCts.Token);
#endif

await server.WaitForExit;

// Save workspace cache on shutdown
try
{
	var sm = server.Services.GetService<ScriptManager>();
	if (sm is not null && sm.UseWorkspaceCache)
	{
		Log.Information("Saving workspace cache on shutdown...");
		await sm.SaveWorkspaceCacheAsync();
	}
}
catch (Exception ex)
{
	Log.Error(ex, "Failed to save workspace cache on shutdown");
}

#if FLAG_MEMORY_DEBUG
memoryMonitorCts.Cancel();
#endif
disposable?.Dispose();