using GSCode.Data;
using GSCode.NET.LSP;
using GSCode.NET.LSP.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression test: BuildReferenceIndex stores reference SymbolKeys lowercased, so any
/// consumer that looks them up must lowercase its own key too. ReferencesHandler and
/// RenameHandler already do this; DocumentHighlightHandler was missed.
/// </summary>
public class DocumentHighlightCasingTests : IDisposable
{
    private readonly string _root;

    public DocumentHighlightCasingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gscode_highlight_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));

        File.WriteAllText(ScriptPath(), """
            function MyFunction()
            {
            }

            function main()
            {
                MyFunction();
                MyFunction();
            }
            """);
    }

    private string ScriptPath() => Path.Combine(_root, "scripts", "main.gsc");

    [Fact]
    public async Task Highlight_OnMixedCaseFunctionName_FindsAllReadReferences()
    {
        var sm = new ScriptManager();
        var docUri = DocumentUri.FromFileSystemPath(ScriptPath());
        await sm.AddEditorAsync(new TextDocumentItem
        {
            Uri = docUri,
            LanguageId = "gsc",
            Version = 1,
            Text = File.ReadAllText(ScriptPath())
        });

        var handler = new DocumentHighlightHandler(sm, default!);

        // Position is on the "MyFunction" call inside main() (line 6, 0-based; column 4 is
        // inside the identifier).
        var request = new DocumentHighlightParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = docUri },
            Position = new Position(6, 4)
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var highlights = result!.ToList();

        // BUG (pre-fix): only the declaration ("Write") highlight is found; both call-site
        // ("Read") references are silently dropped because the lookup key isn't lowercased.
        Assert.Contains(highlights, h => h.Kind == DocumentHighlightKind.Write);
        Assert.Equal(2, highlights.Count(h => h.Kind == DocumentHighlightKind.Read));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
