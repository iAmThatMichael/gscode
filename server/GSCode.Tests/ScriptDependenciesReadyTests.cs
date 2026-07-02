using GSCode.Data;
using GSCode.Parser;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression tests for the dependencies-ready gate: it must be re-armed on every parse
/// cycle, not just the first, so WaitUntilDependenciesReadyAsync genuinely waits for the
/// current cycle's dependency resolution instead of returning instantly because an earlier
/// cycle already completed it.
/// </summary>
public class ScriptDependenciesReadyTests
{
    [Fact]
    public async Task SecondParseCycle_DependenciesReadyGate_IsNotAlreadyCompleted()
    {
        Script script = new(new Uri("file:///deps_ready_test.gsc"), ScriptLanguage.Gsc);

        // Cycle 1: parse, then simulate ScriptManager resolving dependencies.
        await script.ParseAsync("function main() {}");
        script.SignalDependenciesReady();
        await script.WaitUntilDependenciesReadyAsync(); // must already be complete

        // Cycle 2: re-parse (simulating a second edit), but do NOT signal dependencies
        // ready yet — simulating ScriptManager still awaiting Task.WhenAll(dependencies...).
        await script.ParseAsync("function main() {} function second() {}");

        Task waitTask = script.WaitUntilDependenciesReadyAsync();
        await Task.Delay(50);

        // BUG (pre-fix): waitTask completes instantly because _dependenciesReady was
        // never reset after cycle 1's TrySetResult(), so this assertion fails on main.
        Assert.False(waitTask.IsCompleted);

        // Signalling cycle 2's readiness must be what actually completes the wait.
        script.SignalDependenciesReady();
        await waitTask; // should complete promptly, proving the gate does re-arm correctly
    }
}
