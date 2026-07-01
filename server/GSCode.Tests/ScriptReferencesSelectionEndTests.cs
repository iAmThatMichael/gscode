using GSCode.Data;
using GSCode.NET.LSP;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression tests for the "selection-end" LSP position quirk: when the editor sends the
/// end of an active text selection as the cursor position, that position often lands exactly
/// on the boundary between the selected token and the next one (e.g. right after an identifier,
/// at the start of the following ';'). Go-to-definition already compensates for this via
/// Script.Navigation.AdjustPositionForSelectionEnd; this test proves other position-based
/// lookups (starting with GetLocalVariableReferencesAsync) need the same compensation.
/// </summary>
public class ScriptReferencesSelectionEndTests : IDisposable
{
    private readonly string _root;

    public ScriptReferencesSelectionEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gscode_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // Line 2 (0-indexed): "    x = 1;"   -> 'x' spans columns 4-5
        // Line 3 (0-indexed): "    y = x;"   -> 'x' spans columns 8-9
        File.WriteAllText(ScriptPath("main.gsc"), """
            function main()
            {
                x = 1;
                y = x;
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
    public async Task GetLocalVariableReferencesAsync_SelectionEndPosition_StillFindsReferences()
    {
        var sm = new ScriptManager();
        await sm.AddEditorAsync(OpenItem());
        var script = sm.GetParsedEditor(OpenItem().Uri.ToUri())!;

        // Position(3, 9) is the character immediately AFTER the 'x' on line 3 (i.e. the
        // start of the ';' token) -- exactly what an editor sends as the selection-end
        // position when the user double-clicks/selects the word "x" on that line.
        var selectionEndPosition = new Position(3, 9);

        var refs = await script.GetLocalVariableReferencesAsync(selectionEndPosition, includeDeclaration: true);

        Assert.NotEmpty(refs);
        Assert.Contains(refs, r => r.Start.Line == 2 && r.Start.Character == 4); // declaration site
        Assert.Contains(refs, r => r.Start.Line == 3 && r.Start.Character == 8); // usage site
    }
}
