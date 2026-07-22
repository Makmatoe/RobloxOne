namespace SessionDock.Services;

internal sealed class AccountVerificationGate
{
    private readonly object _sync = new();
    private PendingSuppression? _pending;
    private long _nextSuppressionId;

    internal IDisposable SuppressNextAutomaticVerification(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        lock (_sync)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException(
                    "Only one batch account verification can be prepared at a time.");
            }

            var suppression = new PendingSuppression(
                ++_nextSuppressionId,
                accountKey);
            _pending = suppression;
            return new SuppressionScope(this, suppression.Id);
        }
    }

    internal bool ShouldRunAutomaticVerification(WebSessionToken token)
    {
        lock (_sync)
        {
            if (_pending is not { } suppression ||
                !suppression.AccountKey.Equals(
                    token.AccountKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _pending = null;
            return false;
        }
    }

    private void Clear(long suppressionId)
    {
        lock (_sync)
        {
            if (_pending?.Id == suppressionId)
                _pending = null;
        }
    }

    private sealed class SuppressionScope(
        AccountVerificationGate owner,
        long suppressionId) :
        IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Clear(suppressionId);
        }
    }

    private sealed record PendingSuppression(long Id, string AccountKey);
}
