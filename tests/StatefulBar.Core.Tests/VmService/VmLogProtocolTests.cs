using System.Text.Json;
using StatefulBar.Core.VmService;

namespace StatefulBar.Core.Tests.VmService;

public sealed class VmLogProtocolTests
{
    private const string LogEvent = """
        {"jsonrpc":"2.0","method":"streamNotify","params":{"streamId":"Logging","event":{"type":"Event","kind":"Logging",
        "isolate":{"type":"@Isolate","id":"isolates/42","name":"main"},"timestamp":1,
        "logRecord":{"type":"LogRecord","sequenceNumber":7,"time":1,"level":900,
        "message":{"type":"@Instance","id":"objects/1","kind":"String","class":{"type":"@Class","name":"_OneByteString"},
          "valueAsString":"hello wor","valueAsStringIsTruncated":true,"length":300},
        "loggerName":{"type":"@Instance","id":"objects/2","kind":"String","class":{"type":"@Class","name":"_OneByteString"},"valueAsString":"demo"},
        "zone":{"type":"@Instance","kind":"Null","valueAsString":"null"},
        "error":{"type":"@Instance","id":"objects/3","kind":"PlainInstance","class":{"type":"@Class","name":"StateError"}},
        "stackTrace":{"type":"@Instance","kind":"Null","valueAsString":"null"}}}}}
        """;

    [Fact]
    public void Parse_LoggingEvent_ReadsRecord()
    {
        var logEvent = Assert.IsType<VmLogEvent>(VmLogProtocol.Parse(LogEvent));

        Assert.Equal("isolates/42", logEvent.IsolateId);
        Assert.Equal(900, logEvent.Level);
        Assert.Equal(new VmValue(false, "hello wor", true, "objects/1", "_OneByteString"), logEvent.Message);
        Assert.Equal(new VmValue(false, "demo", false, "objects/2", "_OneByteString"), logEvent.LoggerName);
        Assert.Equal(new VmValue(false, null, false, "objects/3", "StateError"), logEvent.Error);
        Assert.True(logEvent.StackTrace.IsNull);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","method":"streamNotify","params":{"streamId":"Stdout","event":{"kind":"WriteEvent","bytes":"aGk="}}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"streamNotify","params":{"streamId":"Logging","event":{"kind":"Logging"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"somethingElse","params":{}}""")]
    [InlineData("""[1,2]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Parse_OtherMessages_ReturnsNull(string json)
    {
        Assert.Null(VmLogProtocol.Parse(json));
    }

    [Fact]
    public void Parse_InstanceResponse_ReadsValue()
    {
        var response = Assert.IsType<VmResponse>(VmLogProtocol.Parse(
            """{"jsonrpc":"2.0","id":"3","result":{"type":"Instance","id":"objects/1","kind":"String","class":{"name":"_OneByteString"},"valueAsString":"hello world"}}"""));

        Assert.Equal("3", response.Id);
        Assert.False(response.IsError);
        Assert.Equal(new VmValue(false, "hello world", false, "objects/1", "_OneByteString"), response.Value);
    }

    [Fact]
    public void Parse_NonInstanceResponse_HasNoValue()
    {
        var response = Assert.IsType<VmResponse>(VmLogProtocol.Parse("""{"jsonrpc":"2.0","id":"listen","result":{"type":"Success"}}"""));

        Assert.Equal("listen", response.Id);
        Assert.False(response.IsError);
        Assert.Null(response.Value);
    }

    [Fact]
    public void Parse_ErrorResponse_IsError()
    {
        var response = Assert.IsType<VmResponse>(VmLogProtocol.Parse(
            """{"jsonrpc":"2.0","id":4,"error":{"code":103,"message":"Stream already subscribed"}}"""));

        Assert.Equal("4", response.Id);
        Assert.True(response.IsError);
    }

    [Fact]
    public void Encode_StreamListen()
    {
        var root = JsonDocument.Parse(VmLogProtocol.EncodeStreamListen("listen", VmLogProtocol.LoggingStream)).RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("listen", root.GetProperty("id").GetString());
        Assert.Equal("streamListen", root.GetProperty("method").GetString());
        Assert.Equal("Logging", root.GetProperty("params").GetProperty("streamId").GetString());
    }

    [Fact]
    public void Encode_GetObject()
    {
        var root = JsonDocument.Parse(VmLogProtocol.EncodeGetObject("5", "isolates/42", "objects/1")).RootElement;

        Assert.Equal("getObject", root.GetProperty("method").GetString());
        Assert.Equal("isolates/42", root.GetProperty("params").GetProperty("isolateId").GetString());
        Assert.Equal("objects/1", root.GetProperty("params").GetProperty("objectId").GetString());
    }

    [Fact]
    public void Encode_ToString()
    {
        var p = JsonDocument.Parse(VmLogProtocol.EncodeToString("6", "isolates/42", "objects/3")).RootElement.GetProperty("params");

        Assert.Equal("isolates/42", p.GetProperty("isolateId").GetString());
        Assert.Equal("objects/3", p.GetProperty("targetId").GetString());
        Assert.Equal("toString", p.GetProperty("selector").GetString());
        Assert.Equal(0, p.GetProperty("argumentIds").GetArrayLength());
    }
}
