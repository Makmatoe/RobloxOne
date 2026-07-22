using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ShutdownTimeBudgetTests
{
    [Fact]
    public void TryGetRemaining_UsesOneDeadlineAcrossSequentialPhases()
    {
        var elapsed = TimeSpan.Zero;
        var budget = new ShutdownTimeBudget(
            TimeSpan.FromSeconds(2),
            () => elapsed);

        Assert.True(budget.TryGetRemaining(out var drainBudget));
        Assert.Equal(TimeSpan.FromSeconds(2), drainBudget);

        elapsed = TimeSpan.FromMilliseconds(750);
        Assert.True(budget.TryGetRemaining(out var saveBudget));
        Assert.Equal(TimeSpan.FromMilliseconds(1250), saveBudget);

        elapsed = TimeSpan.FromMilliseconds(1900);
        Assert.True(budget.TryGetRemaining(out var cleanupBudget));
        Assert.Equal(TimeSpan.FromMilliseconds(100), cleanupBudget);

        elapsed = TimeSpan.FromSeconds(2);
        Assert.False(budget.TryGetRemaining(out var exhaustedBudget));
        Assert.Equal(TimeSpan.Zero, exhaustedBudget);
    }
}
