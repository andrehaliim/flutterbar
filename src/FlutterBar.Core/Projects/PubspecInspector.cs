namespace FlutterBar.Core.Projects;

public static class PubspecInspector
{
    /// <summary>
    /// True when the top-level "dependencies:" block has a "flutter:" key followed by "sdk: flutter".
    /// Line/indent based; no YAML parser.
    /// </summary>
    public static bool IsFlutterApp(string pubspecYamlText)
    {
        var inDependencies = false;
        var childIndent = -1;
        var inFlutter = false;

        foreach (var rawLine in pubspecYamlText.Split('\n'))
        {
            var line = StripComment(rawLine.TrimEnd('\r', ' ', '\t'));
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = line.Length - line.TrimStart(' ', '\t').Length;
            var content = line.Trim();

            if (indent == 0)
            {
                inDependencies = content == "dependencies:";
                childIndent = -1;
                inFlutter = false;
                continue;
            }

            if (!inDependencies)
            {
                continue;
            }

            if (childIndent < 0)
            {
                childIndent = indent;
            }

            if (indent == childIndent)
            {
                inFlutter = content == "flutter:";
            }
            else if (inFlutter && indent > childIndent && content == "sdk: flutter")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <c>build_runner</c> is a key of the top-level <c>dependencies:</c> or <c>dev_dependencies:</c> block.</summary>
    public static bool UsesBuildRunner(string pubspecYamlText)
    {
        var inDependencies = false;
        var childIndent = -1;

        foreach (var rawLine in pubspecYamlText.Split('\n'))
        {
            var line = StripComment(rawLine.TrimEnd('\r', ' ', '\t'));
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = line.Length - line.TrimStart(' ', '\t').Length;
            var content = line.Trim();

            if (indent == 0)
            {
                inDependencies = content is "dependencies:" or "dev_dependencies:";
                childIndent = -1;
                continue;
            }

            if (!inDependencies)
            {
                continue;
            }

            if (childIndent < 0)
            {
                childIndent = indent;
            }

            if (indent == childIndent && content.StartsWith("build_runner:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }
        return line;
    }
}
