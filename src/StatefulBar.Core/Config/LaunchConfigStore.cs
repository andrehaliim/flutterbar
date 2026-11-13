using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StatefulBar.Core.IO;

namespace StatefulBar.Core.Config;

public static class LaunchConfigStore
{
    private const string DefaultProgram = "lib/main.dart";

    /// <summary>JSONC: comments and trailing commas are allowed.</summary>
    internal static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Where launch.json is written: <c>.statefulbar/launch.json</c>.</summary>
    public static string PathFor(string projectRoot) =>
        Path.Combine(projectRoot, ".statefulbar", "launch.json");

    /// <summary>The folder name before the rename to StatefulBar (2026-11); still read when the new file is missing.</summary>
    public static string LegacyPathFor(string projectRoot) =>
        Path.Combine(projectRoot, ".flutterbar", "launch.json");

    /// <summary>
    /// The file to read or open: <see cref="PathFor"/> when it exists, else the legacy <c>.flutterbar/launch.json</c> when
    /// that exists, else <see cref="PathFor"/>. Touches the disk.
    /// </summary>
    public static string ResolvePath(string projectRoot)
    {
        var path = PathFor(projectRoot);
        if (File.Exists(path))
        {
            return path;
        }
        var legacy = LegacyPathFor(projectRoot);
        return File.Exists(legacy) ? legacy : path;
    }

    public static LaunchConfig CreateDefault() =>
        new("default", DefaultProgram, null, RunMode.Debug, []);

    /// <summary>Always reads from disk; nothing is cached.</summary>
    public static LaunchConfigLoadResult Load(string projectRoot)
    {
        var path = ResolvePath(projectRoot);
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
            return new LaunchConfigLoadResult(true, [], [new ConfigError($"Invalid JSON: {ex.Message}", null, line)]);
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

    /// <summary>
    /// Writes <see cref="PathFor"/> atomically. Comments in an existing file are lost. A legacy <c>.flutterbar</c> file is
    /// left alone; the new file takes precedence from then on.
    /// </summary>
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
            errors.Add(new ConfigError("The file content must be a JSON object.", null, null));
            return configs;
        }

        if (!root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v)
            || v != 1)
        {
            errors.Add(new ConfigError("\"version\" must be 1.", "version", null));
        }

        if (!root.TryGetProperty("configurations", out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() == 0)
        {
            errors.Add(new ConfigError("\"configurations\" must contain at least one config.", "configurations", null));
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
                errors.Add(new ConfigError("Each config must be an object.", prefix, null));
                continue;
            }

            var name = ReadString(item, "name", prefix, errors, out var nameTypeOk);
            if (nameTypeOk && string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new ConfigError("\"name\" is required.", $"{prefix}.name", null));
                name = null;
            }
            else if (name is not null && !names.Add(name))
            {
                errors.Add(new ConfigError($"Name \"{name}\" is already used by another config.", $"{prefix}.name", null));
            }

            var program = ReadString(item, "program", prefix, errors, out var programTypeOk) ?? DefaultProgram;
            if (programTypeOk)
            {
                if (string.IsNullOrWhiteSpace(program))
                {
                    errors.Add(new ConfigError("\"program\" must not be empty.", $"{prefix}.program", null));
                }
                else if (!File.Exists(Path.Combine(projectRoot, program)))
                {
                    errors.Add(new ConfigError($"Program file \"{program}\" not found.", $"{prefix}.program", null));
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
                            $"Unknown mode \"{modeText}\". Use debug, profile or release.",
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
            errors.Add(new ConfigError($"\"{property}\" must be a string.", $"{prefix}.{property}", null));
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
            errors.Add(new ConfigError("\"args\" must be a list of strings.", $"{prefix}.args", null));
            return args;
        }
        var i = 0;
        foreach (var arg in value.EnumerateArray())
        {
            if (arg.ValueKind != JsonValueKind.String)
            {
                errors.Add(new ConfigError("Each argument must be a string.", $"{prefix}.args[{i}]", null));
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
