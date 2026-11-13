using System.Diagnostics;
using System.Runtime.InteropServices;
using StatefulBar.Core.Config;
using StatefulBar.Core.Ipc;
using StatefulBar.Core.Logging;
using StatefulBar.Core.Processes;
using StatefulBar.Core.Sessions;

namespace StatefulBar.Core.Tests.Integration;

/// <summary>
/// xunit v2 has no dynamic skip: skipped at discovery when <c>STATEFULBAR_IT_DEVICE</c> is empty, so the normal
/// <c>dotnet test</c> stays fast. Run: <c>$env:STATEFULBAR_IT_DEVICE="chrome"; dotnet test --filter Category=Integration</c>.
/// </summary>
public sealed class EndToEndFactAttribute : FactAttribute
{
    public const string DeviceVariable = "STATEFULBAR_IT_DEVICE";

    public EndToEndFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DeviceVariable)))
        {
            Skip = $"Set {DeviceVariable} (e.g. chrome) to run the end-to-end session test against tests/sample_app.";
        }
    }
}

/// <summary>Real <c>fvm flutter run --machine</c> against tests/sample_app. Records the root process of every launch.</summary>
[Trait("Category", "Integration")]
public sealed class EndToEndTests
{
    private static readonly TimeSpan RunningTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromSeconds(10);

    private sealed class RecordingLauncher(IProcessLauncher inner) : IProcessLauncher
    {
        public List<int> RootPids { get; } = [];

        public IProcessHandle Start(ProcessSpec spec)
        {
            var handle = inner.Start(spec);
            lock (RootPids)
            {
                RootPids.Add(handle.Id);
            }
            return handle;
        }
    }

    private sealed class StaticStatus : IStatusProvider
    {
        public StatusInfo Current() => new("Running", "sample_app", null, "default", Device(), 0);
    }

    private static string Device() => Environment.GetEnvironmentVariable(EndToEndFactAttribute.DeviceVariable)!.Trim();

    private static string ProjectRoot => Path.Combine(TestPaths.RepoRoot, "tests", "sample_app");

    private static RunRequest BuildRequest()
    {
        var config = LaunchConfigStore.CreateDefault();
        var args = FlutterCommandBuilder.BuildRunArgs(config, Device());
        var tool = new FlutterToolLocator(new PathResolver(), File.Exists).Resolve(ProjectRoot, args);
        Assert.True(tool.Spec is not null, tool.Error);
        return new RunRequest(ProjectRoot, config, Device(), tool.Spec);
    }

    private static async Task StartAndWaitRunningAsync(RunSession session)
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(SessionSnapshot s)
        {
            if (s.State == SessionState.Running)
            {
                running.TrySetResult();
            }
            else if (s.State == SessionState.Failed)
            {
                running.TrySetException(new InvalidOperationException("Session failed: " + s.FailureMessage));
            }
        }

        session.Changed += OnChanged;
        try
        {
            await session.StartAsync(BuildRequest());
            // The state may already be Running/Failed if it changed before the handler attached.
            OnChanged(session.Snapshot);
            await running.Task.WaitAsync(RunningTimeout);
        }
        finally
        {
            session.Changed -= OnChanged;
        }
    }

    [EndToEndFact]
    public async Task FullSession_ReloadRestartLogsStop_LeavesNoOrphans()
    {
        var log = new LogBuffer();
        var launcher = new RecordingLauncher(new ProcessLauncher());
        var session = new RunSession(launcher, log);
        var handler = new CommandHandler(session, log, new StaticStatus(), () => { });
        List<int> descendants = [];
        try
        {
            await StartAndWaitRunningAsync(session);
            Assert.Equal(SessionState.Running, session.Snapshot.State);

            // On web, hot reload is a restart and answers code 0, so only the exit code is asserted.
            var reload = await handler.HandleAsync(new IpcRequest("reload", null), CancellationToken.None).WaitAsync(CommandTimeout);
            Assert.True(reload.ExitCode == ExitCodes.Ok, "reload: " + reload.Message);

            var restart = await handler.HandleAsync(new IpcRequest("restart", null), CancellationToken.None).WaitAsync(CommandTimeout);
            Assert.True(restart.ExitCode == ExitCodes.Ok, "restart: " + restart.Message);

            var logs = await handler.HandleAsync(
                new IpcRequest("logs", new Dictionary<string, string> { ["sinceLastReload"] = "true" }),
                CancellationToken.None);
            Assert.Equal(ExitCodes.Ok, logs.ExitCode);
            Assert.NotNull(logs.Data);
            Assert.True(logs.Data.Value.GetProperty("lines").GetArrayLength() > 0, "logs --since-last-reload was empty");

            descendants = ProcessTree.DescendantsOf(launcher.RootPids);
            Assert.NotEmpty(descendants);

            var stop = await handler.HandleAsync(new IpcRequest("stop", null), CancellationToken.None).WaitAsync(CommandTimeout);
            Assert.Equal(ExitCodes.Ok, stop.ExitCode);
            Assert.Equal(SessionState.Idle, session.Snapshot.State);
        }
        finally
        {
            await session.DisposeAsync();
        }

        await AssertAllGoneAsync(launcher.RootPids.Concat(descendants));
    }

    [EndToEndFact]
    public async Task DisposeWhileRunning_NoOrphans()
    {
        var log = new LogBuffer();
        var launcher = new RecordingLauncher(new ProcessLauncher());
        var session = new RunSession(launcher, log);
        List<int> descendants;
        try
        {
            await StartAndWaitRunningAsync(session);
            descendants = ProcessTree.DescendantsOf(launcher.RootPids);
            Assert.NotEmpty(descendants);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }

        await session.DisposeAsync();
        Assert.Equal(SessionState.Idle, session.Snapshot.State);
        await AssertAllGoneAsync(launcher.RootPids.Concat(descendants));
    }

    private static async Task AssertAllGoneAsync(IEnumerable<int> pids)
    {
        var tracked = pids.Distinct().ToList();
        var deadline = Stopwatch.StartNew();
        List<string> alive;
        do
        {
            alive = tracked.Select(Describe).Where(d => d is not null).Select(d => d!).ToList();
            if (alive.Count == 0)
            {
                return;
            }
            await Task.Delay(250);
        }
        while (deadline.Elapsed < OrphanGrace);

        Assert.Fail("Orphan processes still alive after " + OrphanGrace.TotalSeconds + " s: " + string.Join(", ", alive));
    }

    private static string? Describe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited ? null : $"{p.ProcessName}({pid})";
        }
        catch (ArgumentException)
        {
            // No process with this id any more: it is gone.
            return null;
        }
    }

    private static class ProcessTree
    {
        private const uint Th32csSnapProcess = 0x2;

        public static List<int> DescendantsOf(IEnumerable<int> roots)
        {
            var parents = Snapshot();
            var result = new List<int>();
            var queue = new Queue<int>(roots);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                foreach (var (pid, ppid) in parents)
                {
                    if (ppid == parent && !result.Contains(pid))
                    {
                        result.Add(pid);
                        queue.Enqueue(pid);
                    }
                }
            }
            return result;
        }

        private static List<(int Pid, int ParentPid)> Snapshot()
        {
            var list = new List<(int, int)>();
            var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snap == nint.Zero || snap == -1)
            {
                throw new InvalidOperationException("CreateToolhelp32Snapshot failed: " + Marshal.GetLastWin32Error());
            }
            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                for (var ok = Process32First(snap, ref entry); ok; ok = Process32Next(snap, ref entry))
                {
                    list.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
                }
            }
            finally
            {
                CloseHandle(snap);
            }
            return list;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public nuint DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}
