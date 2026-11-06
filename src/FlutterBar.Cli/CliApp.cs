using System.Globalization;
using System.Text.Json;
using FlutterBar.Core.Ipc;

namespace FlutterBar.Cli;

public static class CliApp
{
    private static readonly TimeSpan ReloadTimeout = TimeSpan.FromSeconds(65);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private const string UsageText = """
        Usage:
          flutterbar status
          flutterbar reload
          flutterbar restart
          flutterbar stop
          flutterbar logs [--errors] [--tail N] [--since-last-reload]

        Semua perintah mendukung --json.
        """;

    public static async Task<int> RunAsync(string[] args, IIpcClient client, TextWriter stdout, TextWriter stderr)
    {
        var json = args.Contains("--json");
        var rest = args.Where(a => a != "--json").ToArray();
        if (rest.Length == 0)
        {
            return Usage(stderr, null);
        }

        var command = rest[0];
        if (command == "run")
        {
            stderr.WriteLine("Perintah 'run' sengaja tidak didukung: jalankan app lewat tombol ▶ di FlutterBar.");
            return ExitCodes.Usage;
        }

        IpcRequest request;
        switch (command)
        {
            case "status" or "reload" or "restart" or "stop":
                if (rest.Length > 1)
                {
                    return Usage(stderr, $"Argumen tidak dikenal: {rest[1]}");
                }
                request = new IpcRequest(command, null);
                break;
            case "logs":
                if (!TryParseLogs(rest.AsSpan(1), out var logArgs, out var error))
                {
                    return Usage(stderr, error);
                }
                request = new IpcRequest(command, logArgs);
                break;
            default:
                return Usage(stderr, $"Perintah tidak dikenal: {command}");
        }

        var timeout = command is "reload" or "restart" ? ReloadTimeout : DefaultTimeout;
        var response = await client.SendAsync(request, timeout, CancellationToken.None);
        if (response is null)
        {
            stderr.WriteLine("FlutterBar tidak berjalan. Jalankan FlutterBar terlebih dahulu.");
            return ExitCodes.NotRunning;
        }

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(response, IpcJson.Options));
        }
        else
        {
            PrintHuman(command, response, stdout, stderr);
        }
        return response.ExitCode;
    }

    private static int Usage(TextWriter stderr, string? problem)
    {
        if (problem is not null)
        {
            stderr.WriteLine(problem);
        }
        stderr.WriteLine(UsageText);
        return ExitCodes.Usage;
    }

    private static bool TryParseLogs(ReadOnlySpan<string> args, out Dictionary<string, string> result, out string error)
    {
        result = [];
        error = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--errors":
                    result["errors"] = "true";
                    break;
                case "--since-last-reload":
                    result["sinceLastReload"] = "true";
                    break;
                case "--tail":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    {
                        error = "--tail membutuhkan angka bulat positif";
                        return false;
                    }
                    result["tail"] = args[++i];
                    break;
                default:
                    error = $"Argumen tidak dikenal: {args[i]}";
                    return false;
            }
        }
        return true;
    }

    private static void PrintHuman(string command, IpcResponse response, TextWriter stdout, TextWriter stderr)
    {
        switch (command)
        {
            case "status":
                if (response.Data is { ValueKind: JsonValueKind.Object } status)
                {
                    stdout.WriteLine(FormatStatus(status));
                }
                if (!response.Ok && response.Message.Length > 0)
                {
                    stderr.WriteLine(response.Message);
                }
                return;
            case "logs" when response.Ok:
                if (response.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("lines", out var lines))
                {
                    foreach (var line in lines.EnumerateArray())
                    {
                        stdout.WriteLine(line.GetProperty("text").GetString());
                    }
                }
                return;
        }

        var writer = response.Ok ? stdout : stderr;
        if (response.Message.Length > 0)
        {
            writer.WriteLine(response.Message);
        }
    }

    private static string FormatStatus(JsonElement status)
    {
        var parts = new List<string> { Text(status, "state") ?? "?" };
        foreach (var key in new[] { "project", "branch", "config", "device" })
        {
            if (Text(status, key) is { Length: > 0 } value)
            {
                parts.Add(value);
            }
        }
        var errors = status.TryGetProperty("errorCount", out var count) && count.TryGetInt32(out var n) ? n : 0;
        parts.Add($"{errors} error");
        return string.Join(" · ", parts);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
