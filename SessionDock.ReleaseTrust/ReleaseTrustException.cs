namespace SessionDock.ReleaseTrust;

public sealed class ReleaseTrustException : Exception
{
    public ReleaseTrustException(string message)
        : base(message)
    {
    }

    public ReleaseTrustException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
