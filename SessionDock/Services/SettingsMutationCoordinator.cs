using SessionDock.Models;

namespace SessionDock.Services;

internal sealed class SettingsMutationCoordinator
{
    private readonly object _turnLock = new();
    private readonly AppSettings _settings;
    private readonly SerializedSettingsWriter _writer;
    private Task _turnTail = Task.CompletedTask;
    private Task? _completionTask;
    private bool _acceptingCommits = true;
    private int _pendingCommits;

    internal SettingsMutationCoordinator(
        AppSettings settings,
        SerializedSettingsWriter writer)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    internal bool HasPendingCommits =>
        Volatile.Read(ref _pendingCommits) != 0;

    internal Task<SettingsMutationResult> CommitAsync(
        Action mutation,
        Action? onCommitted = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        Task predecessor;
        var turnCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_turnLock)
        {
            if (!_acceptingCommits)
                return Task.FromResult(SettingsMutationResult.ClosedResult);
            predecessor = _turnTail;
            _turnTail = turnCompletion.Task;
            Interlocked.Increment(ref _pendingCommits);
        }

        return CommitInTurnAsync(
            predecessor,
            turnCompletion,
            mutation,
            onCommitted);
    }

    internal Task CompleteAsync(Func<AppSettings> createFinalSettings)
    {
        ArgumentNullException.ThrowIfNull(createFinalSettings);
        lock (_turnLock)
        {
            if (_completionTask is not null)
                return _completionTask;

            _acceptingCommits = false;
            var predecessor = _turnTail;
            var turnCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _turnTail = turnCompletion.Task;
            Interlocked.Increment(ref _pendingCommits);
            _completionTask = CompleteInTurnAsync(
                predecessor,
                turnCompletion,
                createFinalSettings);
            return _completionTask;
        }
    }

    private async Task<SettingsMutationResult> CommitInTurnAsync(
        Task predecessor,
        TaskCompletionSource turnCompletion,
        Action mutation,
        Action? onCommitted)
    {
        try
        {
            await predecessor;
            var result = await SettingsMutation.TryCommitAsync(
                _settings,
                mutation,
                _writer.SaveAsync);
            if (result.Committed)
                onCommitted?.Invoke();
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCommits);
            turnCompletion.TrySetResult();
        }
    }

    private async Task CompleteInTurnAsync(
        Task predecessor,
        TaskCompletionSource turnCompletion,
        Func<AppSettings> createFinalSettings)
    {
        try
        {
            // Closing admission must return immediately; snapshot creation and
            // writer enqueueing happen asynchronously behind accepted turns.
            await Task.Yield();
            await predecessor;
            await _writer.SaveAsync(createFinalSettings());
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCommits);
            turnCompletion.TrySetResult();
        }
    }
}
