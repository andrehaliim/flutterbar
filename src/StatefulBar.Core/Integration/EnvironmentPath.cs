namespace StatefulBar.Core.Integration;

/// <summary>
/// "Rescan" re-reads PATH: a process keeps the PATH it was started with, so tools installed or added to PATH after
/// StatefulBar started would stay invisible until a restart. The fresh value is built like Windows does for a new logon
/// (machine PATH, then user PATH, environment-expanded), and entries that exist only in the current process (e.g. added
/// by the terminal that started StatefulBar) are kept at the end.
/// </summary>
public static class EnvironmentPath
{
    public static string Compose(string? machine, string? user, string? current, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value, bool doExpand)
        {
            foreach (var raw in (value ?? string.Empty).Split(';'))
            {
                var segment = (doExpand ? expand(raw) : raw).Trim();
                var key = UserPathEditor.Normalize(segment);
                if (key.Length > 0 && seen.Add(key))
                {
                    result.Add(segment.Trim('"').Trim());
                }
            }
        }

        Add(machine, doExpand: true);
        Add(user, doExpand: true);
        Add(current, doExpand: false);
        return string.Join(';', result);
    }
}
