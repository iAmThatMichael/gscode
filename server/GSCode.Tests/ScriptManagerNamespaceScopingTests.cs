using GSCode.Data;
using GSCode.NET.LSP;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// End-to-end regression tests for issue #71: namespaced calls must resolve against the
/// script's direct #using dependencies only. A namespace that merely exists elsewhere in
/// the workspace (transitively imported, or indexed in the background) is still an error.
/// </summary>
public class ScriptManagerNamespaceScopingTests : IDisposable
{
    private readonly string _root;

    public ScriptManagerNamespaceScopingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gscode_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "scripts", "zm"));

        File.WriteAllText(ScriptPath("lib_c.gsc"), """
            #namespace cns;

            function c_func()
            {
            }
            """);

        File.WriteAllText(ScriptPath("lib_b.gsc"), """
            #using scripts\zm\lib_c;

            #namespace bns;

            function b_func()
            {
                cns::c_func();
            }
            """);

        File.WriteAllText(ScriptPath("main.gsc"), """
            #using scripts\zm\lib_b;

            function main()
            {
                bns::b_func();
                bns::missing_func();
                cns::c_func();
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

    private static bool HasCode(Diagnostic d, GSCErrorCodes code)
        => NamespaceDiagnosticsTests.HasCode(d, code);

    [Fact]
    public async Task TransitiveNamespace_IsNotImported_EmitsUnknownNamespace()
    {
        var sm = new ScriptManager();

        var diagnostics = (await sm.AddEditorAsync(OpenItem("main.gsc"))).ToList();

        // bns is directly imported and b_func exists: no error on that call
        Assert.DoesNotContain(diagnostics, d =>
            HasCode(d, GSCErrorCodes.UnknownNamespace) && d.Message.Contains("'bns'"));

        // bns::missing_func: the namespace is known but the function isn't
        Assert.Contains(diagnostics, d =>
            HasCode(d, GSCErrorCodes.NamespaceDoesNotContainFunction) && d.Message.Contains("'missing_func'"));

        // cns is only available transitively through lib_b — main.gsc does not #using lib_c,
        // so the call must be flagged
        Assert.Contains(diagnostics, d =>
            HasCode(d, GSCErrorCodes.UnknownNamespace) && d.Message.Contains("'cns'"));
    }

    [Fact]
    public async Task WorkspaceIndexedNamespace_IsNotImported_EmitsUnknownNamespace()
    {
        var sm = new ScriptManager();

        // Index the whole workspace first (signature-only, as done at startup for game
        // script roots) so the registry knows every namespace, including cns.
        await sm.IndexWorkspaceAsync(_root, signatureOnly: true);

        Assert.Contains("cns", sm.SymbolRegistry.GetAllNamespaces("gsc"));

        var diagnostics = (await sm.AddEditorAsync(OpenItem("main.gsc"))).ToList();

        // Registry knowledge must not suppress the missing-import diagnostic
        Assert.Contains(diagnostics, d =>
            HasCode(d, GSCErrorCodes.UnknownNamespace) && d.Message.Contains("'cns'"));
    }

    [Fact]
    public async Task SignatureOnlyIndexing_PopulatesRegistryForQuickFixes()
    {
        var sm = new ScriptManager();
        await sm.IndexWorkspaceAsync(_root, signatureOnly: true);

        // The quick-fix lookup for cns::c_func must find lib_c.gsc even though nothing imports it
        var exactFiles = sm.SymbolRegistry.FindFilesForNamespacedFunction("cns", "c_func");
        Assert.Contains(exactFiles, f => f.EndsWith("lib_c.gsc", StringComparison.OrdinalIgnoreCase));

        // And the namespace-level fallback finds it too
        var nsFiles = sm.SymbolRegistry.FindFilesForNamespace("cns");
        Assert.Contains(nsFiles, f => f.EndsWith("lib_c.gsc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NamespaceCompletion_OffersWorkspaceNamespaces_AndAutoUsingEdit()
    {
        var sm = new ScriptManager();
        await sm.IndexWorkspaceAsync(_root, signatureOnly: true);
        await sm.AddEditorAsync(OpenItem("main.gsc"));

        var script = sm.GetParsedEditor(DocumentUri.FromFileSystemPath(ScriptPath("main.gsc")).ToUri());
        Assert.NotNull(script);

        // Inside main()'s body, on the "cns" of "cns::c_func();" (line 6, 0-based)
        var namespaceCompletions = await script!.GetCompletionAsync(new Position(6, 5), CancellationToken.None);
        Assert.NotNull(namespaceCompletions);
        Assert.Contains(namespaceCompletions!.Items, i => i.Label == "cns" && i.Kind == CompletionItemKind.Module);

        // On the member position "cns::c_|func" — function completion for an unimported
        // namespace must carry an additional edit inserting the #using directive.
        var memberCompletions = await script.GetCompletionAsync(new Position(6, 10), CancellationToken.None);
        Assert.NotNull(memberCompletions);

        var cFuncItem = memberCompletions!.Items.FirstOrDefault(i => i.Label == "c_func");
        Assert.NotNull(cFuncItem);
        Assert.NotNull(cFuncItem!.AdditionalTextEdits);

        var edit = cFuncItem.AdditionalTextEdits!.First();
        Assert.Contains(@"#using scripts\zm\lib_c;", edit.NewText);
        // Alphabetical: lib_c sorts after the existing lib_b directive on line 0 → inserted on line 1
        Assert.Equal(1, edit.Range.Start.Line);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
