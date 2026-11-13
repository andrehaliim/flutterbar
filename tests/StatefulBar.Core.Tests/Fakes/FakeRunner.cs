using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Fakes;

/// <summary>Answers every <see cref="RunAsync"/> through <paramref name="handler"/> and records the calls.</summary>
public sealed class FakeRunner(Func<ProcessSpec, ProcessResult> handler) : IProcessRunner
{
    private readonly List<ProcessSpec> _calls = [];

    public IReadOnlyList<ProcessSpec> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_calls)
        {
            _calls.Add(spec);
        }
        return Task.FromResult(handler(spec));
    }
}

/// <summary>PATH lookup from a fixed map.</summary>
public sealed class FakePaths(Dictionary<string, string> map) : IPathResolver
{
    public string? Find(string command) => map.GetValueOrDefault(command);

    public void ClearCache()
    {
    }
}
