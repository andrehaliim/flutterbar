using System.Globalization;
using System.Text.Json;
using FlutterBar.Core.IO;

namespace FlutterBar.Core.Settings;

public sealed class AppSettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlutterBar", "settings.json");

    /// <summary>
    /// Missing file: defaults, no warning. Corrupt file: renamed to settings.json.bak-yyyyMMddHHmmss,
    /// defaults returned and <paramref name="warning"/> set. Other IO errors propagate.
    /// </summary>
    public AppSettings Load(out string? warning)
    {
        warning = null;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return new AppSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new AppSettings();
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions);
        }
        catch (JsonException)
        {
            settings = null;
        }

        if (settings is not null)
        {
            return settings;
        }

        var backupPath = path + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        File.Move(path, backupPath, overwrite: true);
        warning = $"settings.json rusak, dicadangkan ke {Path.GetFileName(backupPath)} dan diganti default";
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
