using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FlutterBar.Core.Config;

/// <summary>A run config found in an IDE. <paramref name="Source"/> is "idea" or "vscode".</summary>
public sealed record ImportCandidate(LaunchConfig Config, string Source, bool AlreadyExists);

/// <summary>Reads Android Studio / VS Code Flutter run configs. IDE files are never written.</summary>
public static class IdeConfigImporter
{
    private const string DefaultProgram = "lib/main.dart";
    private const string IdeaProjectDirPrefix = "$PROJECT_DIR$/";
    private const string VscodeWorkspacePrefix = "${workspaceFolder}/";

    /// <exception cref="InvalidDataException">A present IDE file is malformed; the message names the file.</exception>
    public static IReadOnlyList<ImportCandidate> Discover(string projectRoot, IReadOnlyList<LaunchConfig> existing)
    {
        var found = new List<(LaunchConfig Config, string Source)>();

        var ideaFiles = new List<string> { Path.Combine(projectRoot, ".idea", "workspace.xml") };
        var runDir = Path.Combine(projectRoot, ".run");
        if (Directory.Exists(runDir))
        {
            ideaFiles.AddRange(Directory.GetFiles(runDir, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        foreach (var file in ideaFiles)
        {
            if (File.Exists(file))
            {
                found.AddRange(ReadIdea(file).Select(c => (c, "idea")));
            }
        }

        var launch = Path.Combine(projectRoot, ".vscode", "launch.json");
        if (File.Exists(launch))
        {
            found.AddRange(ReadVscode(launch).Select(c => (c, "vscode")));
        }

        var names = existing.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return found.Select(f => new ImportCandidate(f.Config, f.Source, names.Contains(f.Config.Name))).ToList();
    }

    /// <summary>Existing configs followed by the selected ones. Selected names already in
    /// <paramref name="existing"/> are skipped; names colliding among selected get " (source)".</summary>
    public static IReadOnlyList<LaunchConfig> Merge(IReadOnlyList<LaunchConfig> existing, IReadOnlyList<ImportCandidate> selected)
    {
        var result = new List<LaunchConfig>(existing);
        var taken = existing.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in selected)
        {
            var name = candidate.Config.Name;
            if (taken.Contains(name))
            {
                continue;
            }
            if (added.Contains(name))
            {
                var suffixed = $"{name} ({candidate.Source})";
                var n = 2;
                while (taken.Contains(suffixed) || added.Contains(suffixed))
                {
                    suffixed = $"{name} ({candidate.Source} {n++})";
                }
                name = suffixed;
            }
            added.Add(name);
            result.Add(candidate.Config with { Name = name });
        }
        return result;
    }

    private static List<LaunchConfig> ReadIdea(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"File XML tidak valid: {path} ({ex.Message})", ex);
        }

        var configs = new List<LaunchConfig>();
        foreach (var el in doc.Descendants("configuration")
                     .Where(e => (string?)e.Attribute("type") == "FlutterRunConfigurationType"))
        {
            var name = (string?)el.Attribute("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string? Option(string key) => el.Elements("option")
                .FirstOrDefault(o => (string?)o.Attribute("name") == key)?.Attribute("value")?.Value;

            var program = Option("filePath") ?? "";
            if (program.StartsWith(IdeaProjectDirPrefix, StringComparison.Ordinal))
            {
                program = program[IdeaProjectDirPrefix.Length..];
            }
            if (string.IsNullOrWhiteSpace(program))
            {
                program = DefaultProgram;
            }

            var buildFlavor = Option("buildFlavor");
            var (flavor, rest) = LiftFlavor(ShellSplitter.Split(Option("additionalArgs") ?? ""));
            if (!string.IsNullOrEmpty(buildFlavor))
            {
                flavor = buildFlavor;
            }

            configs.Add(new LaunchConfig(name, program, flavor, RunMode.Debug, rest));
        }
        return configs;
    }

    private static List<LaunchConfig> ReadVscode(string path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path), LaunchConfigStore.JsoncOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"File JSON tidak valid: {path} ({ex.Message})", ex);
        }

        using (doc)
        {
            var configs = new List<LaunchConfig>();
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("configurations", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return configs;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || GetString(item, "type") != "dart")
                {
                    continue;
                }
                var name = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var program = GetString(item, "program") ?? "";
                if (program.StartsWith(VscodeWorkspacePrefix, StringComparison.Ordinal))
                {
                    program = program[VscodeWorkspacePrefix.Length..];
                }
                if (string.IsNullOrWhiteSpace(program))
                {
                    program = DefaultProgram;
                }

                var mode = GetString(item, "flutterMode")?.ToLowerInvariant() switch
                {
                    "profile" => RunMode.Profile,
                    "release" => RunMode.Release,
                    _ => RunMode.Debug,
                };

                var args = new List<string>();
                if (item.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                {
                    args.AddRange(argsEl.EnumerateArray()
                        .Where(a => a.ValueKind == JsonValueKind.String)
                        .Select(a => a.GetString()!));
                }
                var (flavor, rest) = LiftFlavor(args);
                configs.Add(new LaunchConfig(name, program, flavor, mode, rest));
            }
            return configs;
        }
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Removes "--flavor X" / "--flavor=X" pairs; the last one found is the flavor.</summary>
    private static (string? Flavor, List<string> Remaining) LiftFlavor(IReadOnlyList<string> args)
    {
        string? flavor = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--flavor" && i + 1 < args.Count)
            {
                flavor = args[++i];
            }
            else if (arg.StartsWith("--flavor=", StringComparison.Ordinal))
            {
                flavor = arg["--flavor=".Length..];
            }
            else
            {
                rest.Add(arg);
            }
        }
        return (string.IsNullOrEmpty(flavor) ? null : flavor, rest);
    }
}
