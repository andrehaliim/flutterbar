using FlutterBar.Core.Integration;
using FlutterBar.Core.Ipc;

namespace FlutterBar.Core.Tests.Integration;

public sealed class StartupGuardTests
{
    [Fact]
    public void UnhandledUiException_BeforeStartupCompleted_ExitsProcess() =>
        Assert.Equal(UnhandledAction.ExitProcess, StartupGuard.OnUnhandledUiException(startupCompleted: false));

    [Fact]
    public void UnhandledUiException_AfterStartupCompleted_KeepsRunning() =>
        Assert.Equal(UnhandledAction.KeepRunning, StartupGuard.OnUnhandledUiException(startupCompleted: true));

    [Fact]
    public void FailedStartupExitCode_IsFailed() => Assert.Equal(ExitCodes.Failed, StartupGuard.FailedExitCode);

    private sealed class SequenceClient(params IpcResponse?[] answers) : IIpcClient
    {
        private int _next;

        public int Calls => _next;

        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            Assert.Equal("show", request.Command);
            var answer = _next < answers.Length ? answers[_next] : null;
            _next++;
            return Task.FromResult(answer);
        }
    }

    private static readonly IpcResponse Ok = new(true, ExitCodes.Ok, null, "");

    [Fact]
    public async Task RequestShow_RetriesWhilePipeIsMissing()
    {
        var client = new SequenceClient(null, null, Ok);

        var (response, attempts) = await StartupGuard.RequestShowAsync(client, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Same(Ok, response);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RequestShow_GivesUpAfterTheBudget()
    {
        var client = new SequenceClient();
        var started = DateTime.UtcNow;

        var (response, attempts) = await StartupGuard.RequestShowAsync(client, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Null(response);
        Assert.True(attempts >= 2, $"attempts={attempts}");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RequestShow_AnswerOnFirstTry_NoRetry()
    {
        var client = new SequenceClient(Ok);

        var (_, attempts) = await StartupGuard.RequestShowAsync(client, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Equal(1, attempts);
    }
}
