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
