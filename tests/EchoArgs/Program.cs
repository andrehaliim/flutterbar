using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Test helper for FlutterBar process tests.
//   (default)       prints the received args as one JSON array line
//   --interactive   echoes every stdin line as "echo:<line>" until EOF
//   --hang          sleeps forever
//   --spawn-child   starts a child "echoargs --interactive", prints the child PID, waits on stdin
//   --spawn-detached-child
//                   starts a long-lived child "echoargs --hang" that does not share our stdio pipes (like a daemon:
//                   emulator, adb server), prints its PID, waits on stdin, then exits leaving the child running
//   --fake-daemon [--ignore-stop]
//                   minimal "flutter run --machine": starts a detached child (as above), prints app.start,
//                   "grandchild:<pid>" and app.started, then answers app.stop by exiting 0 (unless --ignore-stop)
Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

if (args.Length == 1 && args[0] == "--interactive")
{
    string? line;
    while ((line = Console.In.ReadLine()) is not null)
    {
        Console.Out.WriteLine("echo:" + line);
        Console.Out.Flush();
    }
    return 0;
}

if (args.Length == 1 && args[0] == "--hang")
{
    Thread.Sleep(Timeout.Infinite);
}

if (args.Length == 1 && args[0] == "--spawn-child")
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!, "--interactive")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    using var child = Process.Start(psi)!;
    Console.Out.WriteLine(child.Id);
    Console.Out.Flush();
    while (Console.In.ReadLine() is not null)
    {
    }
    return 0;
}

if (args.Length == 1 && args[0] == "--spawn-detached-child")
{
    Console.Out.WriteLine(Detached.Start());
    Console.Out.Flush();
    while (Console.In.ReadLine() is not null)
    {
    }
    return 0;
}

if (args.Length >= 1 && args[0] == "--fake-daemon")
{
    var ignoreStop = args.Contains("--ignore-stop");
    var pid = Detached.Start();
    Console.Out.WriteLine("[{\"event\":\"app.start\",\"params\":{\"appId\":\"fake\",\"supportsRestart\":true}}]");
    Console.Out.WriteLine("grandchild:" + pid);
    Console.Out.WriteLine("[{\"event\":\"app.started\",\"params\":{\"appId\":\"fake\"}}]");
    Console.Out.Flush();
    string? request;
    while ((request = Console.In.ReadLine()) is not null)
    {
        if (!ignoreStop && request.Contains("\"app.stop\"", StringComparison.Ordinal))
        {
            var id = JsonDocument.Parse(request).RootElement[0].GetProperty("id").GetInt32();
            Console.Out.WriteLine($"[{{\"id\":{id},\"result\":true}}]");
            Console.Out.Flush();
            return 0;
        }
    }
    return 0;
}

Console.Out.WriteLine(JsonSerializer.Serialize(args));
Console.Out.Flush();
return 0;

/// <summary>Starts "echoargs --hang" the way a daemon is started: it holds none of our stdio pipes.</summary>
internal static class Detached
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint HandleFlagInherit = 1;

    public static int Start()
    {
        // Process.Start inherits every inheritable handle; our stdio pipes from the test must not reach the child,
        // or the test's stdout pipe would stay open (no EOF) for as long as the child lives.
        foreach (var std in new[] { StdInput, StdOutput, StdError })
        {
            SetHandleInformation(GetStdHandle(std), HandleFlagInherit, 0);
        }
        var psi = new ProcessStartInfo(Environment.ProcessPath!, "--hang")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var child = Process.Start(psi)!;
        return child.Id;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
