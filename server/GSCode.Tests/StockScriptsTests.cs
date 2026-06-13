using GSCode.NET.LSP;
using Xunit;

namespace GSCode.Tests;

public class StockScriptsTests
{
    [Theory]
    [InlineData(@"scripts\shared\util_shared.gsc")]
    [InlineData("scripts/shared/ai/zombie_utility.gsc")]
    [InlineData(@"scripts\zm\_zm_utility.gsc")]
    [InlineData(@"SCRIPTS\CODESCRIPTS\STRUCT.GSC")]
    public void IsStockScript_KnownStockScripts_ReturnsTrue(string relativePath)
    {
        Assert.True(StockScripts.IsStockScript(relativePath));
    }

    [Theory]
    [InlineData(@"scripts\zm\my_custom_utils.gsc")]
    [InlineData(@"scripts\shared\my_team_shared.gsc")]
    [InlineData("")]
    public void IsStockScript_CustomScripts_ReturnsFalse(string relativePath)
    {
        Assert.False(StockScripts.IsStockScript(relativePath));
    }
}
