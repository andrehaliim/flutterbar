using System.Text;
using System.Text.Json;

namespace StatefulBar.Core.VmService;

/// <summary>
/// A Dart object as the VM service describes it (<c>@Instance</c>). <c>Text</c> is <c>valueAsString</c>, which the
/// service cuts short for long strings (<c>Truncated</c>) and omits for plain objects such as an <c>Exception</c>.
/// </summary>
public sealed record VmValue(bool IsNull, string? Text, bool Truncated, string? ObjectId, string? ClassName)
{
    public static VmValue Null { get; } = new(true, null, false, null, null);
}

/// <summary>One parsed VM service message; anything FlutterBar does not use parses to null.</summary>
public abstract record VmMessage;

/// <summary>A <c>Logging</c> stream event: one <c>dart:developer</c> <c>log()</c> call.</summary>
public sealed record VmLogEvent(string IsolateId, VmValue Message, VmValue LoggerName, int Level, VmValue Error, VmValue StackTrace)
    : VmMessage;

/// <summary>The answer to a request. <c>Value</c> is set when the result is an instance.</summary>
public sealed record VmResponse(string Id, VmValue? Value, bool IsError) : VmMessage;

/// <summary>The part of the Dart VM service protocol (JSON-RPC 2.0 over a WebSocket) needed to read <c>log()</c> output.</summary>
public static class VmLogProtocol
{
    public const string LoggingStream = "Logging";

    public static string EncodeStreamListen(string id, string streamId) =>
        Encode(id, "streamListen", writer => writer.WriteString("streamId", streamId));

    /// <summary>Fetches an object in full; for a string the result holds the untruncated <c>valueAsString</c>.</summary>
    public static string EncodeGetObject(string id, string isolateId, string objectId) =>
        Encode(id, "getObject", writer =>
        {
            writer.WriteString("isolateId", isolateId);
            writer.WriteString("objectId", objectId);
        });

    /// <summary>Calls <c>toString()</c> on an object; the result is a string instance.</summary>
    public static string EncodeToString(string id, string isolateId, string targetId) =>
        Encode(id, "invoke", writer =>
        {
            writer.WriteString("isolateId", isolateId);
            writer.WriteString("targetId", targetId);
            writer.WriteString("selector", "toString");
            writer.WriteStartArray("argumentIds");
            writer.WriteEndArray();
        });

    /// <summary>Parses one WebSocket text message; null for invalid JSON and for messages FlutterBar does not use.</summary>
    public static VmMessage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (root.TryGetProperty("method", out var method))
            {
                return method.ValueKind == JsonValueKind.String && method.GetString() == "streamNotify"
                    ? ParseLogEvent(root)
                    : null;
            }
            return ParseResponse(root);
        }
        catch (JsonException)
        {
            // By contract an unparseable message is simply not one FlutterBar uses.
            return null;
        }
    }

    private static VmLogEvent? ParseLogEvent(JsonElement root)
    {
        if (!TryGetObject(root, "params", out var p)
            || Str(p, "streamId") != LoggingStream
            || !TryGetObject(p, "event", out var e)
            || !TryGetObject(e, "logRecord", out var record))
        {
            return null;
        }
        var isolateId = TryGetObject(e, "isolate", out var isolate) ? Str(isolate, "id") : null;
        var level = record.TryGetProperty("level", out var l) && l.TryGetInt32(out var parsed) ? parsed : 0;
        return new VmLogEvent(
            isolateId ?? string.Empty,
            Value(record, "message"),
            Value(record, "loggerName"),
            level,
            Value(record, "error"),
            Value(record, "stackTrace"));
    }

    private static VmResponse? ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idProp))
        {
            return null;
        }
        var id = idProp.ValueKind switch
        {
            JsonValueKind.String => idProp.GetString(),
            JsonValueKind.Number => idProp.GetRawText(),
            _ => null,
        };
        if (id is null)
        {
            return null;
        }
        if (root.TryGetProperty("error", out _))
        {
            return new VmResponse(id, null, true);
        }
        var value = TryGetObject(root, "result", out var result) && Str(result, "type") is "Instance" or "@Instance"
            ? ToValue(result)
            : null;
        return new VmResponse(id, value, false);
    }

    private static VmValue Value(JsonElement parent, string name) =>
        TryGetObject(parent, name, out var instance) ? ToValue(instance) : VmValue.Null;

    private static VmValue ToValue(JsonElement instance)
    {
        if (Str(instance, "kind") == "Null")
        {
            return VmValue.Null;
        }
        var className = TryGetObject(instance, "class", out var cls) ? Str(cls, "name") : null;
        var truncated = instance.TryGetProperty("valueAsStringIsTruncated", out var t) && t.ValueKind == JsonValueKind.True;
        return new VmValue(false, Str(instance, "valueAsString"), truncated, Str(instance, "id"), className);
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Encode(string id, string method, Action<Utf8JsonWriter> writeParams)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("id", id);
            writer.WriteString("method", method);
            writer.WriteStartObject("params");
            writeParams(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
