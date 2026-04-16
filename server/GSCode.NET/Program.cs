using GSCode.NET;
using GSCode.NET.LSP;
using GSCode.Parser.SPA;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using CommandLine;
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

using var server = new GsCodeLanguageServer(input, output);

server.Start();
Log.Information("Language server connected successfully!");

#if FLAG_MEMORY_DEBUG
// Memory monitoring
var memoryMonitorCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
	// Cache the process handle once — Process.GetCurrentProcess() re-enumerates all system processes on each call
	var process = Process.GetCurrentProcess();

	while (!memoryMonitorCts.Token.IsCancellationRequested)
	{
		try
		{
			process.Refresh();
			var memoryMB = process.WorkingSet64 / 1024.0 / 1024.0;
			var privateMemoryMB = process.PrivateMemorySize64 / 1024.0 / 1024.0;

			Log.Information("Memory Usage - Working Set: {WorkingSet:F2} MB, Private: {Private:F2} MB",
				memoryMB, privateMemoryMB);

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

await server.WaitForExitAsync();

#if FLAG_MEMORY_DEBUG
memoryMonitorCts.Cancel();
#endif

// Clean up stream disposable
disposable?.Dispose();