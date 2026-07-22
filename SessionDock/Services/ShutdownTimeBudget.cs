using System.Diagnostics;

namespace SessionDock.Services;

internal sealed class ShutdownTimeBudget
{
    private readonly Func<TimeSpan> _getElapsed;
    private readonly TimeSpan _total;

    internal ShutdownTimeBudget(TimeSpan total)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            total,
            TimeSpan.Zero);
        _total = total;
        var stopwatch = Stopwatch.StartNew();
        _getElapsed = () => stopwatch.Elapsed;
    }

    internal ShutdownTimeBudget(
        TimeSpan total,
        Func<TimeSpan> getElapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            total,
            TimeSpan.Zero);
        _total = total;
        _getElapsed = getElapsed ??
            throw new ArgumentNullException(nameof(getElapsed));
    }

    internal bool TryGetRemaining(out TimeSpan remaining)
    {
        remaining = _total - _getElapsed();
        if (remaining > TimeSpan.Zero)
            return true;
        remaining = TimeSpan.Zero;
        return false;
    }
}
