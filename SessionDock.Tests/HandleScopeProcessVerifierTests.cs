using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeProcessVerifierTests
{
    private static readonly DateTimeOffset ProcessStart =
        new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private const int ProcessId = 2468;
    private const int SessionId = 4;
    private const string ExpectedPath =
        @"C:\TestData\Programs\HandleScope\Api\HandleScope.Api.exe";

    [Fact]
    public void MatchesExpectedProcess_ExactCurrentIdentity_IsAccepted()
    {
        var connection = CreateConnection(ProcessStart.AddSeconds(3));
        var process = CreateProcess();

        var accepted = HandleScopeProcessVerifier.MatchesExpectedProcess(
            connection,
            ExpectedPath,
            SessionId,
            process,
            ProcessStart.AddMinutes(1));

        Assert.True(accepted);
    }

    [Theory]
    [InlineData(@"C:\Temp\HandleScope.Api.exe", "HandleScope.Api", SessionId, ProcessId)]
    [InlineData(ExpectedPath, "HandleScope.Api-copy", SessionId, ProcessId)]
    [InlineData(ExpectedPath, "HandleScope.Api", SessionId + 1, ProcessId)]
    [InlineData(ExpectedPath, "HandleScope.Api", SessionId, ProcessId + 1)]
    public void MatchesExpectedProcess_MismatchedIdentity_IsRejected(
        string actualPath,
        string processName,
        int sessionId,
        int processId)
    {
        var connection = CreateConnection(ProcessStart.AddSeconds(3));
        var process = CreateProcess(
            processId: processId,
            processName: processName,
            sessionId: sessionId,
            executablePath: actualPath);

        var accepted = HandleScopeProcessVerifier.MatchesExpectedProcess(
            connection,
            ExpectedPath,
            SessionId,
            process,
            ProcessStart.AddMinutes(1));

        Assert.False(accepted);
    }

    [Fact]
    public void MatchesExpectedProcess_StaleDiscoveryBeforeProcessStart_IsRejected()
    {
        var connection = CreateConnection(ProcessStart.AddSeconds(-6));

        var accepted = HandleScopeProcessVerifier.MatchesExpectedProcess(
            connection,
            ExpectedPath,
            SessionId,
            CreateProcess(),
            ProcessStart.AddMinutes(3));

        Assert.False(accepted);
    }

    [Fact]
    public void MatchesExpectedProcess_DelayedDiscoveryAfterSuspend_IsAccepted()
    {
        var connection = CreateConnection(ProcessStart.AddHours(1));

        var accepted = HandleScopeProcessVerifier.MatchesExpectedProcess(
            connection,
            ExpectedPath,
            SessionId,
            CreateProcess(),
            ProcessStart.AddHours(1).AddSeconds(1));

        Assert.True(accepted);
    }

    [Fact]
    public void MatchesExpectedProcess_FutureDiscoveryBeyondClockSkew_IsRejected()
    {
        var now = ProcessStart.AddSeconds(30);
        var connection = CreateConnection(now.AddSeconds(6));

        var accepted = HandleScopeProcessVerifier.MatchesExpectedProcess(
            connection,
            ExpectedPath,
            SessionId,
            CreateProcess(),
            now);

        Assert.False(accepted);
    }

    private static HandleScopeConnection CreateConnection(
        DateTimeOffset startedAtUtc) =>
        new(
            new Uri("http://127.0.0.1:51327/"),
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "v1",
            ProcessId,
            startedAtUtc);

    private static HandleScopeProcessSnapshot CreateProcess(
        int processId = ProcessId,
        string processName = "HandleScope.Api",
        int sessionId = SessionId,
        string executablePath = ExpectedPath) =>
        new(
            processId,
            HasExited: false,
            processName,
            sessionId,
            executablePath,
            ProcessStart);
}
