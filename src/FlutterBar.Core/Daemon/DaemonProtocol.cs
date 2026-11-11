using System.Text.Json;

namespace FlutterBar.Core.Daemon;

/// <summary>Parses and encodes the <c>flutter run --machine</c> line protocol (one JSON array per line).</summary>
public static class DaemonProtocol
{
    /// <summary>
    /// Parses one stdout line. A <c>[...]</c> line can hold several objects; anything that is not a valid JSON
    /// array of objects is returned as a single <see cref="NonJson"/> (this is the protocol contract, not an error).
    /// </summary>
    public static IReadOnlyList<DaemonMessage> ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            return [new NonJson(line)];
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [new NonJson(line)];
            }

            var result = new List<DaemonMessage>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return [new NonJson(line)];
                }
                result.Add(Map(item));
            }
            return result;
        }
        catch (JsonException)
        {
            // Not valid JSON: by contract such a line is plain output, surfaced to the caller as NonJson.
            return [new NonJson(line)];
        }
    }

    public static string EncodeRestart(int id, string appId, bool fullRestart) =>
        Encode(id, "app.restart", writer =>
        {
            writer.WriteString("appId", appId);
            writer.WriteBoolean("fullRestart", fullRestart);
            writer.WriteBoolean("pause", false);
            writer.WriteString("reason", "manual");
        });

    public static string EncodeStop(int id, string appId) =>
        Encode(id, "app.stop", writer => writer.WriteString("appId", appId));

    private static string Encode(int id, string method, Action<Utf8JsonWriter> writeParams)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WriteStartObject("params");
            writeParams(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static DaemonMessage Map(JsonElement obj)
    {
        if (obj.TryGetProperty("event", out var eventProp) && eventProp.ValueKind == JsonValueKind.String)
        {
            var name = eventProp.GetString() ?? string.Empty;
            obj.TryGetProperty("params", out var p);
            return name switch
            {
                "app.start" => new AppStart(Str(p, "appId") ?? string.Empty, Str(p, "deviceId") ?? string.Empty, Bool(p, "supportsRestart"), Str(p, "launchMode") ?? string.Empty),
                "app.started" => new AppStarted(Str(p, "appId") ?? string.Empty),
                "app.log" => new AppLog(Str(p, "appId") ?? string.Empty, Str(p, "log") ?? string.Empty, Bool(p, "error")),
                "app.progress" => new AppProgress(Str(p, "message"), Bool(p, "finished")),
                "app.debugPort" => new AppDebugPort(Str(p, "appId") ?? string.Empty, Str(p, "wsUri") ?? string.Empty),
                "app.stop" => new AppStop(Str(p, "appId") ?? string.Empty, Str(p, "error")),
                "daemon.logMessage" => new DaemonLog(Str(p, "level") ?? string.Empty, Str(p, "message") ?? string.Empty),
                _ => new OtherEvent(name),
            };
        }

        if (obj.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
        {
            int? code = null;
            string? message = null;
            if (obj.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.Object)
            {
                code = resultProp.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : null;
                message = Str(resultProp, "message");
            }

            string? error = null;
            if (obj.TryGetProperty("error", out var errorProp))
            {
                error = errorProp.ValueKind == JsonValueKind.String ? errorProp.GetString() : errorProp.GetRawText();
            }
            return new Response(id, code, message, error);
        }

        return new OtherEvent(string.Empty);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool Bool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
