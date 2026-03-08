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

// Load GSC & CSC API into the SPA
await ScriptAnalyserData.LoadLanguageApiAsync(
	"https://www.gscode.net/api/getLibrary?gameId=t7&languageId=gsc",
	gscApiPath
);
await ScriptAnalyserData.LoadLanguageApiAsync(
	"https://www.gscode.net/api/getLibrary?gameId=t7&languageId=csc",
	cscApiPath
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
				.SetMinimumLevel(LogLevel.Trace)
		)
		.WithServices(x => x.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace)))
		.WithServices(services =>
		{
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
				// Configure logging level based on serverLogLevel setting from initialization options
				string logLevelValue = "off";

				if (request.InitializationOptions is JToken initOptions)
				{
					var gscodeSection = initOptions.SelectToken("gscode");
					if (gscodeSection is not null)
					{
						var logLevelSetting = gscodeSection.SelectToken("serverLogLevel");
						if (logLevelSetting is not null)
						{
							logLevelValue = logLevelSetting.Value<string>()?.ToLowerInvariant() ?? "off";
						}
					}
				}

				var minimumLevel = logLevelValue switch
				{
					"off" => LogEventLevel.Warning,
					"messages" => LogEventLevel.Information,
					"verbose" => LogEventLevel.Debug,
					_ => LogEventLevel.Information
				};

				// Update the logging level switch dynamically
				loggingLevelSwitch.MinimumLevel = minimumLevel;

				Log.Information("Server log level set to: {LogLevel} (mapped to {MinimumLevel})", logLevelValue, minimumLevel);

				// Check workspace indexing mode via InitializationOptions
				var indexingMode = IndexingMode.Off;

				if (request.InitializationOptions is JToken initOptions2)
				{
					var gscodeSection = initOptions2.SelectToken("gscode");
					if (gscodeSection is not null)
					{
						var indexingSetting = gscodeSection.SelectToken("workspaceIndexingMode");
						if (indexingSetting is not null)
						{
							var modeValue = indexingSetting.Value<string>()?.ToLowerInvariant();
							indexingMode = modeValue switch
							{
								"off" => IndexingMode.Off,
								"partial" => IndexingMode.Partial,
								"full" => IndexingMode.Full,
								_ => IndexingMode.Off
							};
						}

						// Read customRawPath from initialization options
						var customRawPathSetting = gscodeSection.SelectToken("customRawPath");
						if (customRawPathSetting is not null)
						{
							var customPath = customRawPathSetting.Value<string>();
							if (!string.IsNullOrWhiteSpace(customPath))
							{
								CompletionConfiguration.CustomRawPath = customPath;
								Log.Information("CustomRawPath set from initialization options: {Path}", customPath);
							}
						}

						// Read allowRawFolderWrites from initialization options
						var allowRawWritesSetting = gscodeSection.SelectToken("allowRawFolderWrites");
						if (allowRawWritesSetting is not null)
						{
							var allowWrites = allowRawWritesSetting.Value<bool>();
							CompletionConfiguration.AllowRawFolderWrites = allowWrites;
							Log.Information("AllowRawFolderWrites set from initialization options: {Value}", allowWrites);
						}
					}
				}

				// Set the configuration
				CompletionConfiguration.WorkspaceIndexingMode = indexingMode;

				if (indexingMode == IndexingMode.Off)
				{
					Log.Information("Workspace indexing is disabled");
					return;
				}

				Log.Information("Workspace indexing is enabled: {Mode} mode", indexingMode);

				var sm = server.Services.GetRequiredService<ScriptManager>();

				// Use a long-lived CTS for indexing; do not tie to Initialize request token
				var indexingCts = new CancellationTokenSource();
				options.RegisterForDisposal(indexingCts);
				var indexingToken = indexingCts.Token;

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
				// Check workspace indexing mode via InitializationOptions
				var indexingMode = IndexingMode.Off;

				if (request.InitializationOptions is JToken initOptions)
				{
					var gscodeSection = initOptions.SelectToken("gscode");
					if (gscodeSection is not null)
					{
						var indexingSetting = gscodeSection.SelectToken("workspaceIndexingMode");
						if (indexingSetting is not null)
						{
							var modeValue = indexingSetting.Value<string>()?.ToLowerInvariant();
							indexingMode = modeValue switch
							{
								"off" => IndexingMode.Off,
								"partial" => IndexingMode.Partial,
								"full" => IndexingMode.Full,
								_ => IndexingMode.Off
							};
						}
					}
				}

				// Re-apply configuration to ensure it's set correctly
				CompletionConfiguration.WorkspaceIndexingMode = indexingMode;

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
				if (!string.IsNullOrWhiteSpace(CompletionConfiguration.CustomRawPath))
				{
					string customPath = CompletionConfiguration.CustomRawPath;
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
		.AddHandler<DidChangeWatchedFilesHandler>();
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
	while (!memoryMonitorCts.Token.IsCancellationRequested)
	{
		try
		{
			var process = Process.GetCurrentProcess();
			var memoryMB = process.WorkingSet64 / 1024.0 / 1024.0;
			var privateMemoryMB = process.PrivateMemorySize64 / 1024.0 / 1024.0;

			// Get ScriptManager instance to access loaded data counts
			var scriptManager = server.Services.GetService<ScriptManager>();
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

			Log.Debug("Memory Usage - Working Set: {WorkingSet:F2} MB, Private: {Private:F2} MB | Functions: {Functions}, Classes: {Classes}, Files: GSC={Gsc} CSC={Csc} GSH={Gsh}, Macros: {Macros}", 
				memoryMB, privateMemoryMB, functionCount, classCount, gscCount, cscCount, gshFilesCount, macroCount);

			await Task.Delay(250, memoryMonitorCts.Token);
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