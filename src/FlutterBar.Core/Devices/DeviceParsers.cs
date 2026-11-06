using System.Text;
using System.Text.Json;

namespace FlutterBar.Core.Devices;

public static class DeviceParsers
{
    private const string Separator = "•";

    /// <summary>Parses <c>flutter devices --machine</c> output. Anything before the JSON array (banner text) is ignored.</summary>
    public static IReadOnlyList<DeviceInfo> ParseDevicesMachine(string json)
    {
        var start = FindArrayStart(json);
        if (start < 0)
        {
            return [];
        }

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json[start..]));
        using var doc = JsonDocument.ParseValue(ref reader);
        var list = new List<DeviceInfo>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = GetString(el, "id");
            if (id is null)
            {
                continue;
            }
            var platform = GetString(el, "targetPlatform") ?? "";
            var isEmulator = el.TryGetProperty("emulator", out var e) && e.ValueKind == JsonValueKind.True;
            var emulatorId = GetString(el, "emulatorId");
            list.Add(new DeviceInfo(id, GetString(el, "name") ?? id, platform, isEmulator, string.IsNullOrWhiteSpace(emulatorId) ? null : emulatorId));
        }
        return list;
    }

    /// <summary>Parses <c>flutter emulators</c> output: rows of <c>Id • Name • Manufacturer • Platform</c>; header, prose and blank lines are skipped.</summary>
    public static IReadOnlyList<EmulatorInfo> ParseEmulators(string text)
    {
        var list = new List<EmulatorInfo>();
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Split(Separator).Select(p => p.Trim()).ToArray();
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                continue;
            }
            if (parts[0].Equals("Id", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            list.Add(new EmulatorInfo(parts[0], parts[1]));
        }
        return list;
    }

    /// <summary>Emulators with no matching running emulator device: by <see cref="DeviceInfo.EmulatorId"/>, else by name ('_' in the AVD id counts as a space).</summary>
    public static IReadOnlyList<EmulatorInfo> Offline(IReadOnlyList<EmulatorInfo> emulators, IReadOnlyList<DeviceInfo> devices)
    {
        var running = devices.Where(d => d.IsEmulator).ToList();
        return [.. emulators.Where(em => !running.Any(d => Matches(em, d)))];
    }

    internal static bool Matches(EmulatorInfo em, DeviceInfo device)
    {
        if (!device.IsEmulator)
        {
            return false;
        }
        if (device.EmulatorId is not null)
        {
            return string.Equals(device.EmulatorId, em.Id, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(device.Name, em.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.Name, em.Id.Replace('_', ' '), StringComparison.OrdinalIgnoreCase);
    }

    private static int FindArrayStart(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var eol = text.IndexOf('\n', pos);
            var end = eol < 0 ? text.Length : eol;
            var lead = pos;
            while (lead < end && char.IsWhiteSpace(text[lead]))
            {
                lead++;
            }
            if (lead < end && text[lead] == '[')
            {
                return lead;
            }
            pos = end + 1;
        }
        return text.IndexOf('[');
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
