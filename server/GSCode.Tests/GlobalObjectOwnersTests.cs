using GSCode.Data;
using GSCode.NET.LSP;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

public class GlobalObjectOwnersTests : IDisposable
{
    private readonly string _root;

    public GlobalObjectOwnersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gscode_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        File.WriteAllText(ScriptPath("main.gsc"), """
            function main()
            {
                level.score = 1;
                custom_obj.score = 2;
            }
            """);
    }

    private string ScriptPath(string fileName) => Path.Combine(_root, fileName);

    private TextDocumentItem OpenItem() => new()
    {
        Uri = DocumentUri.FromFileSystemPath(ScriptPath("main.gsc")),
        LanguageId = "gsc",
        Version = 1,
        Text = File.ReadAllText(ScriptPath("main.gsc"))
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ExtractGlobalFieldAccesses_OnlyTracksKnownGlobalOwners()
    {
        var sm = new ScriptManager();
        await sm.AddEditorAsync(OpenItem());
        var script = sm.GetParsedEditor(OpenItem().Uri.ToUri())!;

        var accesses = script.ExtractGlobalFieldAccesses();

        // "level" is a tracked owner -> indexed
        Assert.Contains(("level", "score"), accesses);
        // "custom_obj" is not a tracked owner -> not indexed
        Assert.DoesNotContain(accesses, a => a.OwnerName == "custom_obj");
    }
}
