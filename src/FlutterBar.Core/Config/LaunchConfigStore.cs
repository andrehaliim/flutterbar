using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlutterBar.Core.IO;

namespace FlutterBar.Core.Config;

public static class LaunchConfigStore
{
    private const string DefaultProgram = "lib/main.dart";

    /// <summary>JSONC: comments and trailing commas are allowed.</summary>
    internal static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string PathFor(string projectRoot) =>
        Path.Combine(projectRoot, ".flutterbar", "launch.json");

    public static LaunchConfig CreateDefault() =>
        new("default", DefaultProgram, null, RunMode.Debug, []);

    /// <summary>Always reads from disk; nothing is cached.</summary>
    public static LaunchConfigLoadResult Load(string projectRoot)
    {
        var path = PathFor(projectRoot);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return new LaunchConfigLoadResult(false, [], []);
        }
        catch (DirectoryNotFoundException)
        {
            return new LaunchConfigLoadResult(false, [], []);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, JsoncOptions);
        }
        catch (JsonException ex)
        {
            // JsonException.LineNumber is 0-based; ConfigError.Line is 1-based.
            long? line = ex.LineNumber is { } n ? n + 1 : null;
            return new LaunchConfigLoadResult(true, [], [new ConfigError($"JSON tidak valid: {ex.Message}", null, line)]);
        }

        using (doc)
        {
            var errors = new List<ConfigError>();
            var configs = Validate(doc.RootElement, projectRoot, errors);
            return errors.Count > 0
                ? new LaunchConfigLoadResult(true, [], errors)
                : new LaunchConfigLoadResult(true, configs, []);
        }
    }

    /// <summary>Writes launch.json atomically. Comments in an existing file are lost.</summary>
    public static void Save(string projectRoot, IReadOnlyList<LaunchConfig> configs)
    {
        var path = PathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("configurations");
            foreach (var config in configs)
            {
                writer.WriteStartObject();
                writer.WriteString("name", config.Name);
                writer.WriteString("program", config.Program);
                if (config.Flavor is not null)
                {
                    writer.WriteString("flavor", config.Flavor);
                }
                writer.WriteString("mode", config.Mode.ToString().ToLowerInvariant());
                writer.WriteStartArray("args");
                foreach (var arg in config.Args)
                {
                    writer.WriteStringValue(arg);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static List<LaunchConfig> Validate(JsonElement root, string projectRoot, List<ConfigError> errors)
    {
        var configs = new List<LaunchConfig>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ConfigError("Isi file harus berupa objek JSON.", null, null));
            return configs;
        }

        if (!root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v)
            || v != 1)
        {
            errors.Add(new ConfigError("\"version\" harus bernilai 1.", "version", null));
        }

        if (!root.TryGetProperty("configurations", out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() == 0)
        {
            errors.Add(new ConfigError("\"configurations\" harus berisi minimal satu config.", "configurations", null));
            return configs;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var prefix = $"configurations[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ConfigError("Config harus berupa objek.", prefix, null));
                continue;
            }

            var name = ReadString(item, "name", prefix, errors, out var nameTypeOk);
            if (nameTypeOk && string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new ConfigError("\"name\" wajib diisi.", $"{prefix}.name", null));
                name = null;
            }
            else if (name is not null && !names.Add(name))
            {
                errors.Add(new ConfigError($"Nama \"{name}\" sudah dipakai config lain.", $"{prefix}.name", null));
            }

            var program = ReadString(item, "program", prefix, errors, out var programTypeOk) ?? DefaultProgram;
            if (programTypeOk)
            {
                if (string.IsNullOrWhiteSpace(program))
                {
                    errors.Add(new ConfigError("\"program\" tidak boleh kosong.", $"{prefix}.program", null));
                }
                else if (!File.Exists(Path.Combine(projectRoot, program)))
                {
                    errors.Add(new ConfigError($"File program \"{program}\" tidak ditemukan.", $"{prefix}.program", null));
                }
            }

            var flavor = ReadString(item, "flavor", prefix, errors, out _);

            var mode = RunMode.Debug;
            var modeText = ReadString(item, "mode", prefix, errors, out var modeTypeOk);
            if (modeTypeOk && modeText is not null)
            {
                switch (modeText.ToLowerInvariant())
                {
                    case "debug": mode = RunMode.Debug; break;
                    case "profile": mode = RunMode.Profile; break;
                    case "release": mode = RunMode.Release; break;
                    default:
                        errors.Add(new ConfigError(
                            $"Mode \"{modeText}\" tidak dikenal. Gunakan debug, profile, atau release.",
                            $"{prefix}.mode", null));
                        break;
                }
            }

            var args = ReadArgs(item, prefix, errors);

            if (name is not null)
            {
                configs.Add(new LaunchConfig(name, program, string.IsNullOrEmpty(flavor) ? null : flavor, mode, args));
            }
        }

        return configs;
    }

    private static string? ReadString(JsonElement obj, string property, string prefix, List<ConfigError> errors, out bool typeOk)
    {
        typeOk = true;
        if (!obj.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            typeOk = false;
            errors.Add(new ConfigError($"\"{property}\" harus berupa teks.", $"{prefix}.{property}", null));
            return null;
        }
        return value.GetString();
    }

    private static List<string> ReadArgs(JsonElement obj, string prefix, List<ConfigError> errors)
    {
        var args = new List<string>();
        if (!obj.TryGetProperty("args", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return args;
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ConfigError("\"args\" harus berupa daftar teks.", $"{prefix}.args", null));
            return args;
        }
        var i = 0;
        foreach (var arg in value.EnumerateArray())
        {
            if (arg.ValueKind != JsonValueKind.String)
            {
                errors.Add(new ConfigError("Setiap argumen harus berupa teks.", $"{prefix}.args[{i}]", null));
            }
            else
            {
                args.Add(arg.GetString()!);
            }
            i++;
        }
        return args;
    }
}
