using GSCode.Parser.Util;
using Xunit;

namespace GSCode.Tests;

public class UsingDirectiveHelperTests
{
    [Theory]
    [InlineData(@"C:\game\share\raw\scripts\shared\ai\zombie_utility.gsc", @"scripts\shared\ai\zombie_utility")]
    [InlineData(@"C:/game/usermaps/zm_test/scripts/zm/zm_test.gsc", @"scripts\zm\zm_test")]
    [InlineData(@"scripts\shared\util_shared.gsc", @"scripts\shared\util_shared")]
    [InlineData(@"scripts/shared/util_shared.csc", @"scripts\shared\util_shared")]
    public void ConvertToUsingPath_ProducesDirectivePath(string filePath, string expected)
    {
        Assert.Equal(expected, UsingDirectiveHelper.ConvertToUsingPath(filePath));
    }

    [Fact]
    public void ConvertToUsingPath_NoScriptsSegment_ReturnsNull()
    {
        Assert.Null(UsingDirectiveHelper.ConvertToUsingPath(@"C:\somewhere\else\file.gsc"));
    }

    [Fact]
    public void ExtractUsingsFromContent_FindsPathsAndLines()
    {
        const string content = """
            #using scripts\codescripts\struct;
            #using scripts\shared\util_shared;

            #namespace my_ns;
            """;

        var usings = UsingDirectiveHelper.ExtractUsingsFromContent(content);

        Assert.Equal(2, usings.Count);
        Assert.Equal(@"scripts\codescripts\struct", usings[0].Path);
        Assert.Equal(0, usings[0].Line);
        Assert.Equal(@"scripts\shared\util_shared", usings[1].Path);
        Assert.Equal(1, usings[1].Line);
    }

    [Fact]
    public void GetAlphabeticalInsertPosition_InsertsBeforeFirstGreaterDirective()
    {
        var usings = UsingDirectiveHelper.ExtractUsingsFromContent("""
            #using scripts\shared\array_shared;
            #using scripts\shared\util_shared;
            """);

        // "scripts\shared\flag_shared" sorts between the two
        var position = UsingDirectiveHelper.GetAlphabeticalInsertPosition(usings, @"scripts\shared\flag_shared");

        Assert.Equal(1, position.Line);
        Assert.Equal(0, position.Character);
    }

    [Fact]
    public void GetAlphabeticalInsertPosition_AppendsAfterLastWhenGreatest()
    {
        var usings = UsingDirectiveHelper.ExtractUsingsFromContent("""
            #using scripts\shared\array_shared;
            #using scripts\shared\flag_shared;
            """);

        var position = UsingDirectiveHelper.GetAlphabeticalInsertPosition(usings, @"scripts\zm\zm_utility");

        Assert.Equal(2, position.Line);
    }

    [Fact]
    public void GetAlphabeticalInsertPosition_EmptyList_UsesFallback()
    {
        var position = UsingDirectiveHelper.GetAlphabeticalInsertPosition([], @"scripts\shared\util_shared");

        Assert.Equal(0, position.Line);
        Assert.Equal(0, position.Character);
    }

    [Fact]
    public void ContainsUsing_MatchesIgnoringCaseAndSeparators()
    {
        var usings = UsingDirectiveHelper.ExtractUsingsFromContent(@"#using scripts\shared\Util_Shared;");

        Assert.True(UsingDirectiveHelper.ContainsUsing(usings, "scripts/shared/util_shared"));
        Assert.False(UsingDirectiveHelper.ContainsUsing(usings, @"scripts\shared\other"));
    }
}
