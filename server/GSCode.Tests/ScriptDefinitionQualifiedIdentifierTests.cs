using GSCode.Data;
using GSCode.NET.LSP;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression coverage for go-to-definition on a namespace-qualified call (e.g. "utl::helper()").
/// Script.Navigation.TryGetFunctionOrClassLocation and Script.References.BuildReferenceIndex both
/// independently decide "is this identifier namespace-qualified" from the token stream; this test
/// guards the go-to-definition side while that logic gets unified into one shared helper.
/// </summary>
public class ScriptDefinitionQualifiedIdentifierTests : IDisposable
{
    private readonly string _root;

    public ScriptDefinitionQualifiedIdentifierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gscode_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "scripts", "zm"));

        File.WriteAllText(ScriptPath("lib.gsc"), """
            #namespace utl;

            function helper()
            {
            }
            """);

        File.WriteAllText(ScriptPath("main.gsc"), """
            #using scripts\zm\lib;

            function main()
            {
                utl::helper();
            }
            """);
    }

    private string ScriptPath(string fileName) => Path.Combine(_root, "scripts", "zm", fileName);

    private TextDocumentItem OpenItem(string fileName) => new()
    {
        Uri = DocumentUri.FromFileSystemPath(ScriptPath(fileName)),
        LanguageId = "gsc",
        Version = 1,
        Text = File.ReadAllText(ScriptPath(fileName))
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task GoToDefinition_OnQualifiedFunctionCall_ResolvesToFunctionDeclaration()
    {
        var sm = new ScriptManager();
        await sm.AddEditorAsync(OpenItem("main.gsc"));
        var script = sm.GetParsedEditor(OpenItem("main.gsc").Uri.ToUri())!;

        // Line 4 (0-indexed): "    utl::helper();" -> "helper" spans columns 9-15.
        var location = await script.GetDefinitionAsync(new Position(4, 10), default);

        Assert.NotNull(location);
        Assert.EndsWith("lib.gsc", location!.Uri.ToUri().LocalPath, StringComparison.OrdinalIgnoreCase);
    }
}
