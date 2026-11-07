namespace FlutterBar.Core.Config;

/// <summary>One checklist row of the import dialog.</summary>
public sealed record ImportRow(ImportCandidate Candidate, string Title, string Detail, string ToolTip, bool IsChecked, bool IsEnabled, string? Note);

/// <summary>What the import dialog shows: rows, or an <paramref name="Error"/> message (with the exception for diag.log).</summary>
public sealed record ImportDiscovery(IReadOnlyList<ImportRow> Rows, bool LaunchJsonExists, string? Error, Exception? Exception);

/// <summary>
/// "Import config from IDE" (spec §4.2) without UI: discovery into checklist rows, and applying the selection through
/// <see cref="IdeConfigImporter.Merge"/> and <see cref="LaunchConfigStore.Save"/>. A name already in launch.json is shown
/// as "already exists", unchecked and disabled, because <see cref="IdeConfigImporter.Merge"/> skips existing names anyway. A config
/// whose <c>program</c> file is missing is offered unchecked with <see cref="MissingProgramNote"/>: launch.json validation would reject the whole
/// file because of it.
/// </summary>
public static class ImportPlan
{
    public const string AlreadyExistsNote = "already exists";
    public const string MissingProgramNote = "program file is missing — the config will show an error after import";
    public const string NothingFoundText = "No Flutter configs found in .idea/workspace.xml, .run/*.xml or .vscode/launch.json.";

    public static string SourceLabel(string source) => source switch
    {
        "idea" => "Android Studio",
        "vscode" => "VS Code",
        _ => source,
    };

    /// <param name="programExists">Whether a config's program file exists; null = assume it does.</param>
    public static IReadOnlyList<ImportRow> Rows(IReadOnlyList<ImportCandidate> candidates, Func<LaunchConfig, bool>? programExists = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return [.. candidates.Select(c =>
        {
            var missing = !c.AlreadyExists && programExists is not null && !programExists(c.Config);
            var note = c.AlreadyExists ? AlreadyExistsNote : missing ? MissingProgramNote : null;
            return new ImportRow(c, c.Config.Name, Detail(c), ToolTip(c.Config), IsChecked: !c.AlreadyExists && !missing, IsEnabled: !c.AlreadyExists, note);
        })];
    }

    /// <summary>Reads launch.json and the IDE files (disk I/O: call off the UI thread). Never throws for bad files.</summary>
    public static ImportDiscovery Discover(string projectRoot)
    {
        try
        {
            var existing = LaunchConfigStore.Load(projectRoot);
            if (existing.Errors.Count > 0)
            {
                return new ImportDiscovery([], true, InvalidLaunchJsonText(existing), null);
            }
            var candidates = IdeConfigImporter.Discover(projectRoot, existing.Configs);
            return candidates.Count == 0
                ? new ImportDiscovery([], existing.FileExists, NothingFoundText, null)
                : new ImportDiscovery(Rows(candidates, c => File.Exists(Path.Combine(projectRoot, c.Program))), existing.FileExists, null, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ImportDiscovery([], false, ex.Message, ex);
        }
    }

    /// <summary>
    /// Re-reads launch.json (it may have changed while the dialog was open), merges the selection and saves; nothing is
    /// written when no name is new. Returns the names added (suffixed on collisions).
    /// </summary>
    /// <exception cref="InvalidDataException">launch.json is invalid: it is never overwritten.</exception>
    public static IReadOnlyList<string> Apply(string projectRoot, IReadOnlyList<ImportCandidate> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var existing = LaunchConfigStore.Load(projectRoot);
        if (existing.Errors.Count > 0)
        {
            throw new InvalidDataException(InvalidLaunchJsonText(existing));
        }
        var merged = IdeConfigImporter.Merge(existing.Configs, selected);
        var added = merged.Skip(existing.Configs.Count).Select(c => c.Name).ToList();
        if (added.Count > 0)
        {
            LaunchConfigStore.Save(projectRoot, merged);
        }
        return added;
    }

    private static string InvalidLaunchJsonText(LaunchConfigLoadResult result) =>
        "launch.json is invalid. Fix it first (menu: Open launch.json): " + result.Errors[0].Message;

    private static string Detail(ImportCandidate candidate)
    {
        var config = candidate.Config;
        var parts = new List<string> { SourceLabel(candidate.Source) };
        if (config.Flavor is not null)
        {
            parts.Add("flavor " + config.Flavor);
        }
        parts.Add(config.Mode.ToString().ToLowerInvariant());
        parts.Add(config.Program);
        return string.Join(" · ", parts);
    }

    private static string ToolTip(LaunchConfig config) =>
        config.Args.Count == 0 ? "args: -" : "args: " + string.Join(' ', config.Args);
}
