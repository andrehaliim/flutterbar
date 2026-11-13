using StatefulBar.Cli;
using StatefulBar.Core.Ipc;

namespace StatefulBar.Core.Tests.Cli;

public sealed class CliAppTests
{
    private sealed class FakeClient(IpcResponse? response) : IIpcClient
    {
        public IpcRequest? Request { get; private set; }
        public TimeSpan? Timeout { get; private set; }
        public int Calls { get; private set; }

        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            Request = request;
            Timeout = timeout;
            return Task.FromResult(response);
        }
    }

    private static async Task<(int Code, string Out, string Err, FakeClient Client)> RunAsync(IpcResponse? response, params string[] args)
    {
        var client = new FakeClient(response);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, client, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString(), client);
    }

    private static IpcResponse Success(string message = "ok", object? data = null) =>
        new(true, 0, data is null ? null : IpcJson.ToElement(data), message);

    [Fact]
    public async Task NotRunning_Exit2_PrintsNotRunning()
    {
        var r = await RunAsync(null, "status");

        Assert.Equal(ExitCodes.NotRunning, r.Code);
        Assert.Contains("StatefulBar is not running", r.Err);
    }

    [Fact]
    public async Task Reload_JsonFlag_PrintsResponseJson()
    {
        var response = new IpcResponse(false, 1, IpcJson.ToElement(new { success = false, message = "failed" }), "failed");

        var r = await RunAsync(response, "reload", "--json");

        Assert.Equal(1, r.Code);
        Assert.Equal("{\"ok\":false,\"exitCode\":1,\"data\":{\"success\":false,\"message\":\"failed\"},\"message\":\"failed\"}", r.Out.Trim());
    }

    [Theory]
    [InlineData("reload")]
    [InlineData("restart")]
    public async Task ReloadAndRestart_UseClientTimeout65s(string command)
    {
        var r = await RunAsync(Success("Reloaded"), command);

        Assert.Equal(0, r.Code);
        Assert.Equal(command, r.Client.Request!.Command);
        Assert.Equal(TimeSpan.FromSeconds(65), r.Client.Timeout);
        Assert.Contains("Reloaded", r.Out);
    }

    [Fact]
    public async Task OtherCommands_Use10sTimeout()
    {
        var r = await RunAsync(Success(), "stop");

        Assert.Equal(TimeSpan.FromSeconds(10), r.Client.Timeout);
    }

    [Fact]
    public async Task Reload_Failure_PrintsMessageToStderr_ReturnsServerExitCode()
    {
        var r = await RunAsync(new IpcResponse(false, 4, null, "Timeout reload"), "reload");

        Assert.Equal(4, r.Code);
        Assert.Contains("Timeout reload", r.Err);
    }

    [Fact]
    public async Task Status_HumanOutput()
    {
        var data = new StatusInfo("Running", "gts-mobile", null, "beta", "emulator-5554", 0);

        var r = await RunAsync(Success("", data), "status");

        Assert.Equal(0, r.Code);
        Assert.Equal("Running · gts-mobile · beta · emulator-5554 · 0 error", r.Out.Trim());
    }

    [Fact]
    public async Task Status_HumanOutput_IncludesBranch_AndIdleExit3()
    {
        var data = new StatusInfo("Idle", "gts-mobile", "main", "beta", null, 2);
        var response = new IpcResponse(false, 3, IpcJson.ToElement(data), "No active session");

        var r = await RunAsync(response, "status");

        Assert.Equal(3, r.Code);
        Assert.Equal("Idle · gts-mobile · main · beta · 2 error", r.Out.Trim());
    }

    [Fact]
    public async Task Logs_ParsesFlags()
    {
        var r = await RunAsync(Success("", new { lines = Array.Empty<object>() }), "logs", "--errors", "--tail", "50", "--since-last-reload");

        Assert.Equal(0, r.Code);
        Assert.Equal("logs", r.Client.Request!.Command);
        Assert.Equal("true", r.Client.Request.Args!["errors"]);
        Assert.Equal("50", r.Client.Request.Args["tail"]);
        Assert.Equal("true", r.Client.Request.Args["sinceLastReload"]);
    }

    [Fact]
    public async Task Logs_PrintsTextLines()
    {
        var data = new { lines = new[] { new { seq = 1, time = DateTimeOffset.UtcNow, kind = "app", text = "halo" }, new { seq = 2, time = DateTimeOffset.UtcNow, kind = "error", text = "oops" } } };

        var r = await RunAsync(Success("", data), "logs");

        Assert.Equal(["halo", "oops"], r.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("logs", "--tail")]
    [InlineData("logs", "--tail", "abc")]
    [InlineData("logs", "--bogus")]
    [InlineData("status", "extra")]
    public async Task BadArguments_Exit64_NoRequestSent(params string[] args)
    {
        var r = await RunAsync(Success(), args);

        Assert.Equal(ExitCodes.Usage, r.Code);
        Assert.Equal(0, r.Client.Calls);
        Assert.Contains("Usage", r.Err, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("--json")]
    public async Task NoArgs_OrUnknown_Exit64_PrintsUsage(params string[] args)
    {
        var r = await RunAsync(Success(), args);

        Assert.Equal(ExitCodes.Usage, r.Code);
        Assert.Contains("sbar reload", r.Err);
        Assert.Equal(0, r.Client.Calls);
    }

    [Fact]
    public async Task RunCommand_NotSupported_Exit64()
    {
        var r = await RunAsync(Success(), "run");

        Assert.Equal(ExitCodes.Usage, r.Code);
        Assert.Contains("run", r.Err);
        Assert.Contains("isn't supported", r.Err);
        Assert.Equal(0, r.Client.Calls);
    }
}
