using GSCode.Data;
using GSCode.NET.LSP;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Reproduces a reported bug: go-to-definition on a macro call resolves to the wrong
/// #define in a .gsh that declares multiple macros, when the called macro isn't the
/// first one declared in that file.
/// </summary>
public class MacroGoToDefinitionWrongTargetTests : IDisposable
{
    private readonly string _root;

    public MacroGoToDefinitionWrongTargetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gscode_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "scripts", "shared"));

        // Mirrors shared.gsh: an early simple macro (GAMEMODE_PUBLIC_MATCH-equivalent)
        // followed much later by a parameterised macro (ARRAY_ADD-equivalent).
        File.WriteAllText(ScriptPath("shared.gsh"), """
            #define PLAYER_1 0
            #define PLAYER_2 1

            #define GAMEMODE_PUBLIC_MATCH 0
            #define GAMEMODE_PRIVATE_MATCH 1

            #define ARRAY_ADD(__array,__item) __array[__array.size] = __item;
            """);

        File.WriteAllText(ScriptPath("main.gsc"), """
            #insert scripts\shared\shared.gsh;

            function main()
            {
                ARRAY_ADD(level.arr, 1);
            }
            """);
    }

    private string ScriptPath(string fileName) => Path.Combine(_root, "scripts", "shared", fileName);

    private TextDocumentItem OpenItem(string fileName) => new()
    {
        Uri = DocumentUri.FromFileSystemPath(ScriptPath(fileName)),
        LanguageId = "gsc",
        Version = 1,
        Text = File.ReadAllText(ScriptPath(fileName))
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task GoToDefinition_OnParameterisedMacroCall_ResolvesToItsOwnDefine_NotAnEarlierOne()
    {
        var sm = new ScriptManager();
        await sm.AddEditorAsync(OpenItem("main.gsc"));
        var script = sm.GetParsedEditor(OpenItem("main.gsc").Uri.ToUri())!;

        // Line 4 (0-indexed): "    ARRAY_ADD(level.arr, 1);" -> "ARRAY_ADD" spans columns 4-13.
        var location = await script.GetDefinitionAsync(new Position(4, 8), default);

        Assert.NotNull(location);
        Assert.EndsWith("shared.gsh", location!.Uri.ToUri().LocalPath, StringComparison.OrdinalIgnoreCase);
        // ARRAY_ADD is declared on line 6 (0-indexed) of shared.gsh - not line 3 (GAMEMODE_PUBLIC_MATCH).
        Assert.Equal(6, location.Range.Start.Line);
    }
}
