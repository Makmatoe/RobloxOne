namespace RobloxOneLauncher.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _instanceMutex;
    private readonly EventWaitHandle _activationSignal;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly bool _ownsMutex;
    private Task? _activationListener;
    private bool _disposed;

    public bool IsPrimaryInstance => _ownsMutex;

    public SingleInstanceService(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Any(character =>
                character is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            throw new ArgumentException(
                "The application ID contains an invalid synchronization name character.",
                nameof(applicationId));
        }

        var namePrefix = $@"Local\{applicationId}";
        _activationSignal = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            $@"{namePrefix}.Activate");
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            $@"{namePrefix}.Mutex",
            out _ownsMutex);
    }

    public void NotifyPrimaryInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsMutex)
            _activationSignal.Set();
    }

    public void ListenForActivationRequests(Action activationRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activationRequested);
        if (!_ownsMutex)
            throw new InvalidOperationException(
                "Only the primary instance can listen for activation requests.");
        if (_activationListener is not null)
            throw new InvalidOperationException(
                "The activation listener has already been started.");

        _activationListener = Task.Run(() =>
        {
            var handles = new WaitHandle[]
            {
                _activationSignal,
                _shutdown.Token.WaitHandle
            };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                if (_shutdown.IsCancellationRequested)
                    return;
                activationRequested();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _activationSignal.Set();
        try
        {
            _activationListener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Process exit must continue if the optional listener is stopping.
        }

        if (_ownsMutex)
            _instanceMutex.ReleaseMutex();
        _instanceMutex.Dispose();
        _activationSignal.Dispose();
        _shutdown.Dispose();
    }
}
