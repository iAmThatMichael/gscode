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
		})
		.OnInitialize(async (server, request, ct) =>
		{
			try
			{
				var sm = server.Services.GetRequiredService<ScriptManager>();

				if (request.InitializationOptions is JToken initOptions)
				{
					var gscodeSection = initOptions.SelectToken("gscode");
					if (gscodeSection is not null)
					{
						// Log level
						var logLevelValue = gscodeSection.SelectToken("serverLogLevel")?.Value<string>()?.ToLowerInvariant() ?? "off";
						loggingLevelSwitch.MinimumLevel = logLevelValue switch
						{
							"off" => LogEventLevel.Warning,
							"messages" => LogEventLevel.Information,
							"verbose" => LogEventLevel.Debug,
							_ => LogEventLevel.Information
						};
						Log.Information("Server log level set to: {LogLevel}", logLevelValue);

						// Indexing mode
						var indexingModeStr = gscodeSection.SelectToken("workspaceIndexingMode")?.Value<string>()?.ToLowerInvariant();
						var indexingMode = indexingModeStr switch
						{
							"full" => IndexingMode.Full,
							"partial" => IndexingMode.Partial,
							_ => IndexingMode.Off
						};

						var completionOpts = CompletionOptions.Current;
						completionOpts.WorkspaceIndexingMode = indexingMode;

						// Custom raw path
						var customPath = gscodeSection.SelectToken("customRawPath")?.Value<string>();
						if (!string.IsNullOrWhiteSpace(customPath))
						{
							completionOpts.CustomRawPath = customPath;
							sm.CustomRawPath = customPath;
							Log.Information("CustomRawPath set: {Path}", customPath);
						}

						// Allow raw folder writes
						var allowWrites = gscodeSection.SelectToken("allowRawFolderWrites")?.Value<bool>() ?? false;
						completionOpts.AllowRawFolderWrites = allowWrites;

						Log.Information("Settings: IndexingMode={IndexingMode}, AllowRawFolderWrites={AllowWrites}, CustomRawPath={CustomRawPath}",
							indexingMode, allowWrites, customPath ?? "(none)");
					}
				}

				// Start workspace indexing
				var indexingCts = new CancellationTokenSource();
				options.RegisterForDisposal(indexingCts);
				var indexingToken = indexingCts.Token;

				if (!string.IsNullOrWhiteSpace(CompletionConfiguration.CustomRawPath))
				{
					string rawPath = CompletionConfiguration.CustomRawPath;
					if (Directory.Exists(rawPath))
					{
						Log.Information("Starting workspace indexing for CustomRawPath: {Root}", rawPath);
						_ = Task.Run(() => sm.IndexWorkspaceAsync(rawPath, indexingToken), indexingToken);
					}
				}
				else if (request.RootUri is not null)
				{
					string root = request.RootUri.ToUri().LocalPath;
					if (Directory.Exists(root))
					{
						Log.Information("Starting workspace indexing for: {Root}", root);
						_ = Task.Run(() => sm.IndexWorkspaceAsync(root, indexingToken), indexingToken);
					}
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed during initialization");
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
		.AddHandler<ConfigurationHandler>()
		.AddHandler<DidChangeWatchedFilesHandler>()
		.AddHandler<CodeActionHandler>();

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

			int functionCount = 0, classCount = 0, gscCount = 0, cscCount = 0;

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

			Log.Information(
				"Memory Usage - Working Set: {WorkingSet:F2} MB, Private: {Private:F2} MB | " +
				"Functions: {Functions}, Classes: {Classes}, " +
				"Files: GSC={Gsc} CSC={Csc} GSH={Gsh}, Macros: {Macros}",
				memoryMB, privateMemoryMB,
				functionCount, classCount,
				gscCount, cscCount, macroStats.GshFiles,
				macroStats.TotalMacros);

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
disposable?.Dispose();