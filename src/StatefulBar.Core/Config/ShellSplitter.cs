using System.Text;

namespace StatefulBar.Core.Config;

/// <summary>Splits a command line into arguments: whitespace separates, quotes group.</summary>
public static class ShellSplitter
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var hasToken = false;
        var quote = '\0';

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}
