using GSCode.NET.LSP;
using GSCode.Parser.Lexical;
using GSCode.Parser.Pre;
using Xunit;

namespace GSCode.Tests;

[Collection("NonParallel")]
public class MemoryOptimizationTests
{
    [Fact]
    public void StringPool_Clear_RemovesAllEntries()
    {
        StringPool.Clear();
        StringPool.Intern("test_string_1");
        StringPool.Intern("test_string_2");
        int before = StringPool.Count;

        StringPool.Clear();

        Assert.Equal(0, StringPool.Count);
        Assert.Equal(2, before);
    }

    [Fact]
    public void StringPool_ClearThenIntern_WorksCorrectly()
    {
        StringPool.Clear();
        string interned = StringPool.Intern("repopulate_me");

        Assert.Equal(1, StringPool.Count);
        Assert.Equal("repopulate_me", interned);
    }

    [Fact]
    public void MacroDefinitionCache_Clear_RemovesAllEntries()
    {
        MacroDefinitionCache.Instance.Clear();
        var (totalMacros, trackedFiles, _) = MacroDefinitionCache.Instance.GetDetailedStatistics();

        Assert.Equal(0, totalMacros);
        Assert.Equal(0, trackedFiles);
    }

    [Fact]
    public void ScriptCache_UpdateCache_IncrementalEdit_ReplacesCorrectRange()
    {
        // Arrange: a document with three lines
        var cache = new ScriptCache();
        var docItem = new OmniSharp.Extensions.LanguageServer.Protocol.Models.TextDocumentItem
        {
            Uri = new Uri("file:///test.gsc"),
            Text = "line one\nline two\nline three",
            LanguageId = "gsc",
            Version = 1
        };
        cache.AddToCache(docItem);

        // Act: replace "two" on line 1 with "2"
        var identifier = new OmniSharp.Extensions.LanguageServer.Protocol.Models.OptionalVersionedTextDocumentIdentifier
        {
            Uri = new Uri("file:///test.gsc"),
            Version = 2
        };
        var change = new OmniSharp.Extensions.LanguageServer.Protocol.Models.TextDocumentContentChangeEvent
        {
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(1, 5),  // 'two'
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(1, 8)
            ),
            Text = "2"
        };
        string result = cache.UpdateCache(identifier, [change]);

        // Assert
        Assert.Equal("line one\nline 2\nline three", result);
    }
}
