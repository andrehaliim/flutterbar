using System.Globalization;
using System.Text.Json;
using StatefulBar.Core.IO;

namespace StatefulBar.Core.Settings;

public sealed class AppSettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StatefulBar", "settings.json");

    /// <summary>Where the app kept its settings before the rename to StatefulBar (2026-11).</summary>
    public static string LegacyDefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlutterBar", "settings.json");

    /// <summary>
    /// One-time migration after the rename: copies <paramref name="legacyPath"/> to <paramref name="path"/> when only the
    /// legacy file exists. The legacy file stays as it is. True when a copy was made.
    /// </summary>
    public static bool CopyLegacyIfMissing(string path, string legacyPath)
    {
        if (File.Exists(path) || !File.Exists(legacyPath))
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.Copy(legacyPath, path, overwrite: false);
        return true;
    }

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
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            // Invalid JSON or invalid path keys (rejected by AppSettings.Projects): treat as corrupt,
            // back the file up below and fall back to defaults with a warning.
            settings = null;
        }

        if (settings is not null)
        {
            return settings;
        }

        var backupPath = path + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        File.Move(path, backupPath, overwrite: true);
        warning = $"settings.json is corrupt: backed up to {Path.GetFileName(backupPath)} and replaced with defaults";
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
