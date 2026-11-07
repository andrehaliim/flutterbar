using System.IO;
using System.Text.Json;

namespace DockingSpike;

internal sealed class SpikeSettings
{
    public string Mode { get; set; } = "auto";
    public int? OffsetFromRight { get; set; }
    public bool HideDebug { get; set; }

    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "spike-settings.json");
    static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public static SpikeSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) { Taskbar.Log("settings: none -> mode=auto"); return new(); }
            var s = JsonSerializer.Deserialize<SpikeSettings>(File.ReadAllText(Path), Opts) ?? new();
            if (s.Mode != "manual" || s.OffsetFromRight is null) { s.Mode = "auto"; s.OffsetFromRight = null; }
            Taskbar.Log($"settings: loaded mode={s.Mode} offsetFromRight={s.OffsetFromRight} hideDebug={s.HideDebug}");
            return s;
        }
        catch (Exception ex)
        {
            Taskbar.Log($"settings: load failed ({ex.Message}) -> mode=auto");
            return new();
        }
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Opts)); Taskbar.Log($"settings: saved {JsonSerializer.Serialize(this, Opts)}"); }
        catch (Exception ex) { Taskbar.Log($"settings: save failed: {ex.Message}"); }
    }
}
