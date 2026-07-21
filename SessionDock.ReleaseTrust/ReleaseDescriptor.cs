namespace SessionDock.ReleaseTrust;

public sealed record ReleaseDescriptor(
    int SchemaVersion,
    string Product,
    string Repository,
    string Channel,
    string KeyId,
    string Version,
    string Tag,
    string PublishedAt,
    string PackageFile,
    long PackageSize,
    string PackageSha256,
    string ReleaseNotes,
    string Signature);

public sealed record ReleaseAssetIdentity(
    string Version,
    string FileName,
    long Size,
    string Sha256);

public sealed record VerifiedReleaseDescriptor(
    ReleaseDescriptor Descriptor,
    Version Version,
    DateTimeOffset PublishedAt);
