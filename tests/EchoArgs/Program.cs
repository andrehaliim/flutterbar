using System.Diagnostics;
using System.Text;
using System.Text.Json;

// Test helper for FlutterBar process tests.
//   (default)       prints the received args as one JSON array line
//   --interactive   echoes every stdin line as "echo:<line>" until EOF
//   --spawn-child   starts a child "echoargs --interactive", prints the child PID, waits on stdin
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

Console.Out.WriteLine(JsonSerializer.Serialize(args));
Console.Out.Flush();
return 0;
