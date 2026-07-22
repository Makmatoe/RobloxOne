using SessionDock.Models;

namespace SessionDock.Services;

internal sealed class SerializedSettingsWriter
{
    private readonly object _queueLock = new();
    private readonly Action<AppSettings> _save;
    private Task _queueTail = Task.CompletedTask;
    private long _lastCompletedRevision;
    private long _lastEnqueuedRevision;

    internal SerializedSettingsWriter(Action<AppSettings> save)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
    }

    internal Task SaveAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_queueLock)
        {
            var snapshot = AppSettingsSnapshot.Create(settings);
            var revision = ++_lastEnqueuedRevision;
            var write = SaveAfterAsync(_queueTail, snapshot, revision);
            _queueTail = ObserveForQueueProgress(write);
            return write;
        }
    }

    internal long LastCompletedRevision =>
        Interlocked.Read(ref _lastCompletedRevision);

    internal long LastEnqueuedRevision =>
        Interlocked.Read(ref _lastEnqueuedRevision);

    private async Task SaveAfterAsync(
        Task predecessor,
        AppSettings snapshot,
        long revision)
    {
        await predecessor.ConfigureAwait(false);
        await Task.Run(() => _save(snapshot)).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastCompletedRevision, revision);
    }

    private static Task ObserveForQueueProgress(Task write) =>
        write.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
