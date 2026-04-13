using GSCode.NET;
using GSCode.Parser.SPA;
using GSCode.Parser.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using StreamJsonRpc;
using System.IO.Pipes;
using System.Text;
using CommandLine;
using GSCode.NET.LSP;
using GSCode.NET.LSP.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Diagnostics;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;


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

(Stream input, Stream output, IDisposable? disposable) = await StreamResolver.ResolveAsync(serverOptions, CancellationToken.None);

LanguageServer server = await LanguageServer.From(options =>
{
	options
		.WithInput(input)
		.WithOutput(output)
		.WithConfigurationSection("gscode")
		.ConfigureLogging(
			x => x
				.AddSerilog(Log.Logger)
				.AddLanguageProtocolLogging()
				.SetMinimumLevel(LogLevel.Debug)
		)
		.WithServices(x => x.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug)))
		.WithServices(services =>
		{
			// Register CompletionOptions as singleton so ConfigurationHandler can inject
			// and mutate it; parser-layer code reads the same instance via CompletionOptions.Current.
			services.AddSingleton<CompletionOptions>();
			// Inject ScriptManager with ILanguageServerFacade so it can publish diagnostics during indexing
			services.AddSingleton<ScriptManager>(sp =>
			{
				var logger = sp.GetRequiredService<ILogger<ScriptManager>>();
				var facade = sp.GetRequiredService<ILanguageServerFacade>();
				return new ScriptManager(logger, facade);
			});
			services.AddSingleton(new TextDocumentSelector(
				new TextDocumentFilter { Pattern = "**/*.gsc" },
				new TextDocumentFilter { Pattern = "**/*.csc" },
				new TextDocumentFilter { Pattern = "**/*.gsh" }
			));
		})
		.OnInitialize(async (server, request, ct) =>
		{
			try
			{
				// Publish the DI-managed CompletionOptions as the live instance so all layers
				// (parser, handlers, server) share the exact same object.
				var opts = server.Services.GetRequiredService<CompletionOptions>();
				CompletionOptions.SetCurrent(opts);

				JToken? initOptions = request.InitializationOptions as JToken;

				// Configure logging level
				loggingLevelSwitch.MinimumLevel = InitializationOptionsReader.ParseServerLogLevel(initOptions);
				Log.Information("Server log level switch set to {MinimumLevel}", loggingLevelSwitch.MinimumLevel);

				// Configure workspace options from client initialization options
				opts.WorkspaceIndexingMode = InitializationOptionsReader.ParseWorkspaceIndexingMode(initOptions);
				opts.CustomRawPath         = InitializationOptionsReader.ParseCustomRawPath(initOptions);
				opts.AllowRawFolderWrites  = InitializationOptionsReader.ParseAllowRawFolderWrites(initOptions);

				Log.Information("Workspace indexing mode: {Mode}", opts.WorkspaceIndexingMode);
				if (opts.CustomRawPath is not null)
					Log.Information("CustomRawPath set from initialization options: {Path}", opts.CustomRawPath);
				Log.Information("AllowRawFolderWrites: {Value}", opts.AllowRawFolderWrites);

				// Defer actual indexing to OnInitialized to ensure server is fully ready
				await Task.CompletedTask;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed to process initialization options");
			}
		})
		.OnInitialized(async (server, request, response, ct) =>
		{
			try
			{
				// Read the already-configured options (set during OnInitialize)
				var opts = server.Services.GetRequiredService<CompletionOptions>();
				var indexingMode = opts.WorkspaceIndexingMode;

				// Re-apply to ensure consistency
				opts.WorkspaceIndexingMode = indexingMode;

				if (indexingMode == IndexingMode.Off)
				{
					Log.Information("Workspace indexing disabled in OnInitialized");
					return;
				}

				Log.Information("Starting workspace indexing in {Mode} mode", indexingMode);

				var sm = server.Services.GetRequiredService<ScriptManager>();

				var indexingCts = new CancellationTokenSource();
				options.RegisterForDisposal(indexingCts);
				var indexingToken = indexingCts.Token;

				// If CustomRawPath is set, index that instead of workspace folders
					if (!string.IsNullOrWhiteSpace(opts.CustomRawPath))
					{
						string customPath = opts.CustomRawPath;
						if (Directory.Exists(customPath))
						{
							Log.Information("Starting workspace indexing for CustomRawPath: {Root}", customPath);
							_ = Task.Run(() => sm.IndexWorkspaceAsync(customPath, indexingToken), indexingToken);
						}
						else
						{
							Log.Warning("CustomRawPath is set but directory does not exist: {Path}", customPath);
						}
					}
				else if (request.WorkspaceFolders is not null && request.WorkspaceFolders.Any())
				{
					foreach (var wf in request.WorkspaceFolders)
					{
						string root = wf.Uri.ToUri().LocalPath;
						Log.Information("Starting workspace indexing for: {Root}", root);
						_ = Task.Run(() => sm.IndexWorkspaceAsync(root, indexingToken), indexingToken);
					}
				}
				else if (request.RootUri is not null)
				{
					string root = request.RootUri.ToUri().LocalPath;
					Log.Information("Starting workspace indexing for: {Root}", root);
					_ = Task.Run(() => sm.IndexWorkspaceAsync(root, indexingToken), indexingToken);
				}
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
		.AddHandler<SignatureHelpHandler>()
		.AddHandler<ReferencesHandler>()
		.AddHandler<ConfigurationHandler>()
		.AddHandler<DidChangeWatchedFilesHandler>()
		.AddHandler<CodeActionHandler>();
	// Allow disposal of the stream if required.
	if (disposable is not null)
	{
		options.RegisterForDisposal(disposable);
	}
}).ConfigureAwait(false);


Log.Information("Language server connected successfully!");

#if FLAG_MEMORY_DEBUG
// Memory monitoring
var memoryMonitorCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
	// Cache the process handle once — Process.GetCurrentProcess() re-enumerates all system processes on each call
	var process = Process.GetCurrentProcess();
	var scriptManager = server.Services.GetService<ScriptManager>();

	while (!memoryMonitorCts.Token.IsCancellationRequested)
	{
		try
		{
			process.Refresh();
			var memoryMB = process.WorkingSet64 / 1024.0 / 1024.0;
			var privateMemoryMB = process.PrivateMemorySize64 / 1024.0 / 1024.0;

			int functionCount = 0;
			int classCount = 0;
			int gscCount = 0;
			int cscCount = 0;
			int macroCount = 0;
			int gshFilesCount = 0;

			if (scriptManager != null)
			{
				var symbolCounts = scriptManager.SymbolRegistry.GetCountsByType();
				functionCount = symbolCounts.Functions;
				classCount = symbolCounts.Classes;
				var scriptCounts = scriptManager.GetScriptCountsByType();
				gscCount = scriptCounts.GscFiles;
				cscCount = scriptCounts.CscFiles;
			}

			var macroStats = GSCode.Parser.Pre.MacroDefinitionCache.Instance.GetDetailedStatistics();
			macroCount = macroStats.TotalMacros;
			gshFilesCount = macroStats.GshFiles;

			Log.Information("Memory Usage - Working Set: {WorkingSet:F2} MB, Private: {Private:F2} MB | Functions: {Functions}, Classes: {Classes}, Files: GSC={Gsc} CSC={Csc} GSH={Gsh}, Macros: {Macros}", 
				memoryMB, privateMemoryMB, functionCount, classCount, gscCount, cscCount, gshFilesCount, macroCount);

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

#if FLAG_MEMORY_DEBUG
memoryMonitorCts.Cancel();
#endif