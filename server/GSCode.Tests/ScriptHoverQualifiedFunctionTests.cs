using GSCode.Data;
using GSCode.NET.LSP;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression coverage for hovering the namespace-qualifier half of a qualified call
/// (e.g. the "utl" in "utl::helper()"). Script.Hover.TryGetQualifiedFunctionToken forwards
/// the qualifier token to the function-name token so hover resolves the function, not the
/// (non-existent) namespace symbol.
/// </summary>
public class ScriptHoverQualifiedFunctionTests : IDisposable
{
    private readonly string _root;

    public ScriptHoverQualifiedFunctionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gscode_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        File.WriteAllText(ScriptPath("lib.gsc"), """
            #namespace utl;

            function helper()
            {
            }
            """);

        File.WriteAllText(ScriptPath("main.gsc"), """
            #using lib;

            function main()
            {
                utl::helper();
            }
            """);
    }

    private string ScriptPath(string fileName) => Path.Combine(_root, fileName);

    private TextDocumentItem OpenItem(string fileName) => new()
    {
        Uri = DocumentUri.FromFileSystemPath(ScriptPath(fileName)),
        LanguageId = "gsc",
        Version = 1,
        Text = File.ReadAllText(ScriptPath(fileName))
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task HoveringNamespaceQualifier_ResolvesToFunctionHover()
    {
        var sm = new ScriptManager();
        await sm.AddEditorAsync(OpenItem("main.gsc"));
        var script = sm.GetParsedEditor(OpenItem("main.gsc").Uri.ToUri())!;

        // Line 4 (0-indexed): "    utl::helper();" -> "utl" spans columns 4-7.
        // Hover in the middle of "utl".
        var hover = await script.GetHoverAsync(new Position(4, 5), default);

        Assert.NotNull(hover);
    }
}
