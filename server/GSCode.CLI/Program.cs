using BenchmarkDotNet.Running;
using CommandLine;
using GSCode.NET.LSP;
using GSCode.Parser;
using GSCode.Parser.SPA;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Serilog;
using System.Diagnostics;

namespace GSCode.CLI;

class Program
{
    public class Options
    {
        [Option('p', "parse", Required = false, HelpText = "Parses the provided file path.")]
        public string Parse { get; set; } = default!;

        [Option('i', "index", Required = false, HelpText = "Indexes the provided directory (mimics workspace indexing).")]
        public string Index { get; set; } = default!;

        [Option('b', "benchmark", Required = false, HelpText = "Runs a scene shared benchmark ( do not use).")]
        public bool Benchmark { get; set; }
    }

    static async Task Main(string[] args)
    {
        await CommandLine.Parser.Default.ParseArguments<Options>(args)
               .WithParsedAsync<Options>(async o =>
               {
                   ScriptManager scriptManager = new ScriptManager(new NullLogger<ScriptManager>());

                   if (o.Index != null)
                   {
                       await RunIndexMode(scriptManager, o.Index);
                       return;
                   }

                   if (o.Parse != null)
                   {
                       Console.WriteLine($"Parsing {o.Parse}...");
                       Uri documentUri = new Uri(o.Parse);
                       TextDocumentItem documentItem = new TextDocumentItem()
                       {
                           Uri = documentUri,
                           Text = File.ReadAllText(o.Parse)
                       };

                       // Adding to ScriptManager's cache and getting diagnostics
                       IEnumerable<Diagnostic> diagnostics = await scriptManager.AddEditorAsync(documentItem);

                       Console.WriteLine("Diagnostics:");
                       foreach (var diagnosticsItem in diagnostics)
                       {
                           Console.WriteLine($"{diagnosticsItem.Message}");
                       }

                       Console.WriteLine("Press any key to exit.");
                       Console.ReadKey();
                   }

                   //if (o.Benchmark)
                   //{
                   //    var summary = BenchmarkRunner.Run<Benchmarks>();
                   //}
               });
    }

    static async Task RunIndexMode(ScriptManager scriptManager, string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            Console.Error.WriteLine($"Directory not found: {fullPath}");
            return;
        }

        Console.WriteLine($"Indexing {fullPath}...");
        var sw = Stopwatch.StartNew();

        var process = Process.GetCurrentProcess();
        long memBefore = process.WorkingSet64;

        await scriptManager.IndexWorkspaceAsync(fullPath);

        sw.Stop();

        // Force GC to get accurate post-index memory
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        process.Refresh();
        long memAfter = process.WorkingSet64;

        Console.WriteLine();
        Console.WriteLine($"Indexing completed in {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"Memory before: {memBefore / (1024.0 * 1024.0):F1} MB");
        Console.WriteLine($"Memory after:  {memAfter / (1024.0 * 1024.0):F1} MB");
        Console.WriteLine($"Memory delta:  {(memAfter - memBefore) / (1024.0 * 1024.0):F1} MB");
        Console.WriteLine();
        Console.WriteLine("Press any key to exit (attach profiler now if needed).");
        Console.ReadKey();
    }
}
