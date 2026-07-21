using System.Net;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeApiBootstrapperTests
{
    private const string ValidToken =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task GetExistingAsync_UsesSharedProcessVerifierBeforeNetwork()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-HandleScopeBootstrapper-{Guid.NewGuid():N}");
        var connectionPath = Path.Combine(root, "HandleScope", "connection.json");
        Directory.CreateDirectory(Path.GetDirectoryName(connectionPath)!);
        await File.WriteAllTextAsync(
            connectionPath,
            $$"""
                {
                  "apiVersion": "v1",
                  "baseUrl": "http://127.0.0.1:51327",
                  "token": "{{ValidToken}}",
                  "processId": 2468,
                  "startedAtUtc": "2026-07-21T12:00:00+00:00"
                }
                """,
            TestContext.Current.CancellationToken);

        try
        {
            var inspectionCount = 0;
            var processVerifier = new TestProcessVerifier(_ =>
            {
                inspectionCount++;
                return false;
            });
            using var handler = new FailIfCalledHandler();
            using var client = new HttpClient(handler);
            var bootstrapper = new HandleScopeApiBootstrapper(
                new HandleScopeConnectionLoader(
                    connectionPath,
                    root,
                    isReparsePoint: null),
                client,
                processVerifier);

            var result = await bootstrapper.GetExistingAsync(
                TestContext.Current.CancellationToken);

            Assert.Null(result);
            Assert.Equal(1, inspectionCount);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestProcessVerifier(
        Func<HandleScopeConnection, bool> verifier)
        : IHandleScopeProcessVerifier
    {
        public bool IsExpected(HandleScopeConnection connection) =>
            verifier(connection);

        public bool IsExpectedStartedProcess(int processId) => false;
    }

    private sealed class FailIfCalledHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.InternalServerError));
        }
    }
}
