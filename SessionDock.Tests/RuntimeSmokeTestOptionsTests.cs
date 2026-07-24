using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RuntimeSmokeTestOptionsTests : IDisposable
{
    private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    private readonly List<string> _createdPaths = [];

    [Fact]
    public void TryParse_NoArguments_IsNotRequested()
    {
        var parsed = RuntimeSmokeTestOptions.TryParse([], out var options, out var error);

        Assert.True(parsed);
        Assert.Null(options);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--veloapp-install", "1.2.3")]
    [InlineData("ordinary-file-association-value")]
    public void TryParse_NonSmokeArguments_RemainAvailableToVelopack(
        params string[] arguments)
    {
        var parsed = RuntimeSmokeTestOptions.TryParse(
            arguments,
            out var options,
            out var error);

        Assert.True(parsed);
        Assert.Null(options);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_ValidFreshDirectChild_ReturnsIsolatedOptions()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(
            _temporaryRoot,
            RuntimeSmokeTestOptions.RootNamePrefix + suffix);

        var parsed = RuntimeSmokeTestOptions.TryParse(
            [RuntimeSmokeTestOptions.ArgumentName, root],
            out var options,
            out var error);

        Assert.True(parsed);
        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(root, options.RootDirectory);
        Assert.Equal(
            Path.Combine(root, RuntimeSmokeTestOptions.ResultFileName),
            options.ResultPath);
        Assert.Equal($"SessionDockRuntimeSmoke{suffix}", options.ApplicationId);
    }

    [Theory]
    [InlineData()]
    [InlineData("one", "two")]
    public void TryParse_SmokeSwitchRequiresExactlyOneRoot(
        params string[] trailingArguments)
    {
        var arguments = new[] { RuntimeSmokeTestOptions.ArgumentName }
            .Concat(trailingArguments)
            .ToArray();

        var parsed = RuntimeSmokeTestOptions.TryParse(
            arguments,
            out var options,
            out var error);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_NestedTemporaryPath_IsRejected()
    {
        var root = Path.Combine(
            _temporaryRoot,
            "nested",
            RuntimeSmokeTestOptions.RootNamePrefix + Guid.NewGuid().ToString("N"));

        var parsed = RuntimeSmokeTestOptions.TryParse(
            [RuntimeSmokeTestOptions.ArgumentName, root],
            out var options,
            out _);

        Assert.False(parsed);
        Assert.Null(options);
    }

    [Theory]
    [InlineData("SessionDock-runtime-smoke-not-hex")]
    [InlineData("SessionDock-runtime-smoke-0000000000000000000000000000000")]
    [InlineData("other-00000000000000000000000000000000")]
    public void TryParse_UnexpectedRootName_IsRejected(string rootName)
    {
        var root = Path.Combine(_temporaryRoot, rootName);

        var parsed = RuntimeSmokeTestOptions.TryParse(
            [RuntimeSmokeTestOptions.ArgumentName, root],
            out var options,
            out _);

        Assert.False(parsed);
        Assert.Null(options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryParse_ExistingTarget_IsRejected(bool targetIsFile)
    {
        var root = NewRootPath();
        if (targetIsFile)
            File.WriteAllText(root, "pre-existing data");
        else
            Directory.CreateDirectory(root);

        var parsed = RuntimeSmokeTestOptions.TryParse(
            [RuntimeSmokeTestOptions.ArgumentName, root],
            out var options,
            out _);

        Assert.False(parsed);
        Assert.Null(options);
    }

    [Fact]
    public void TryValidateRoot_RedirectedTemporaryDirectory_IsRejected()
    {
        var root = Path.Combine(
            _temporaryRoot,
            RuntimeSmokeTestOptions.RootNamePrefix + Guid.NewGuid().ToString("N"));

        var valid = RuntimeSmokeTestOptions.TryValidateRoot(
            root,
            _temporaryRoot,
            path => path.Equals(_temporaryRoot, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : throw new FileNotFoundException(),
            out _,
            out _,
            out _);

        Assert.False(valid);
    }

    [Fact]
    public void TryValidateRoot_RedirectedExistingTarget_IsRejected()
    {
        var root = Path.Combine(
            _temporaryRoot,
            RuntimeSmokeTestOptions.RootNamePrefix + Guid.NewGuid().ToString("N"));

        var valid = RuntimeSmokeTestOptions.TryValidateRoot(
            root,
            _temporaryRoot,
            path => path.Equals(_temporaryRoot, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory
                : FileAttributes.Directory | FileAttributes.ReparsePoint,
            out _,
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("redirected", error, StringComparison.OrdinalIgnoreCase);
    }

    private string NewRootPath()
    {
        var path = Path.Combine(
            _temporaryRoot,
            RuntimeSmokeTestOptions.RootNamePrefix + Guid.NewGuid().ToString("N"));
        _createdPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _createdPaths)
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
