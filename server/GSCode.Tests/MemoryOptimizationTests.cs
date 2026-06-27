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
}
