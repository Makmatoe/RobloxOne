using System.Runtime.ExceptionServices;

namespace SessionDock.Services;

internal static class BatchLaunchPipeline
{
    internal static async Task<IReadOnlyList<TResult>> RunAsync<
        TPlan,
        TQueued,
        TStarted,
        TResult>(
        IReadOnlyList<TPlan> plans,
        Func<TPlan, int, CancellationToken, Task<TQueued>> queueAsync,
        Func<TQueued, int, CancellationToken, Task<TStarted>> startAsync,
        Func<TStarted, int, bool, CancellationToken, Task<TResult>> completeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(queueAsync);
        ArgumentNullException.ThrowIfNull(startAsync);
        ArgumentNullException.ThrowIfNull(completeAsync);
        cancellationToken.ThrowIfCancellationRequested();
        if (plans.Count == 0)
            return [];

        using var pipelineCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<TQueued>? pendingQueue = null;
        try
        {
            pendingQueue = StartQueue(0);
            var results = new List<TResult>(plans.Count);
            for (var index = 0; index < plans.Count; index++)
            {
                var currentQueue = pendingQueue ??
                    throw new InvalidOperationException(
                        "The batch queue ended before every item was started.");
                pendingQueue = null;
                var queued = await currentQueue;
                pipelineCancellation.Token.ThrowIfCancellationRequested();

                var started = await startAsync(
                    queued,
                    index,
                    pipelineCancellation.Token);
                var hasNext = index + 1 < plans.Count;
                if (hasNext)
                    pendingQueue = StartQueue(index + 1);

                results.Add(await completeAsync(
                    started,
                    index,
                    hasNext,
                    pipelineCancellation.Token));
            }

            return results;
        }
        catch (Exception exception)
        {
            pipelineCancellation.Cancel();
            Exception? pendingFailure = null;
            if (pendingQueue is not null)
            {
                try
                {
                    await pendingQueue;
                }
                catch (OperationCanceledException) when (
                    pipelineCancellation.IsCancellationRequested)
                {
                    // The look-ahead operation was cancelled during cleanup.
                }
                catch (Exception pendingException)
                {
                    pendingFailure = pendingException;
                }
            }

            if (pendingFailure is not null)
                throw new AggregateException(exception, pendingFailure);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }

        Task<TQueued> StartQueue(int index) =>
            queueAsync(
                plans[index],
                index,
                pipelineCancellation.Token) ??
            throw new InvalidOperationException(
                "Batch queueing returned no task.");
    }
}
