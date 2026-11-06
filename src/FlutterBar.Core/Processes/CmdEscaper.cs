using System.Text;

namespace FlutterBar.Core.Processes;

/// <summary>
/// Builds a <c>cmd.exe</c> argument string for running a batch file with arguments that arrive verbatim.
/// Same approach as the Rust standard library fix for CVE-2024-24576.
/// </summary>
public static class CmdEscaper
{
    private const string SpecialChars = " \t\"&|<>^%!()`;,=";

    /// <summary>
    /// Switches placed before the batch path. Extensions on (needed for <c>%cd:~,%</c>, <c>%*</c>, <c>%~dp0</c>) and delayed
    /// expansion off, so a registry DelayedExpansion=1 cannot expand <c>!VAR!</c> in arguments.
    /// </summary>
    public static readonly IReadOnlyList<string> CmdSwitches = ["/e:ON", "/v:OFF", "/d", "/s", "/c"];

    /// <summary>Returns <c>/e:ON /v:OFF /d /s /c ""batch" arg1 arg2"</c> with every argument escaped.</summary>
    public static string BuildCmdArguments(string batchPath, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(batchPath);
        ArgumentNullException.ThrowIfNull(args);
        RejectNewlines(batchPath);
        if (batchPath.Contains('"'))
        {
            throw new ArgumentException("Batch path must not contain a double quote.");
        }

        var sb = new StringBuilder(string.Join(' ', CmdSwitches)).Append(" \"\"");
        sb.Append(batchPath.Replace("%", "%%cd:~,%")).Append('"');
        foreach (var arg in args)
        {
            RejectNewlines(arg);
            sb.Append(' ');
            AppendArg(sb, arg);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void RejectNewlines(string value)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Arguments for cmd.exe must not contain line breaks.");
        }
    }

    private static void AppendArg(StringBuilder sb, string arg)
    {
        var quote = arg.Length == 0 || arg.AsSpan().IndexOfAny(SpecialChars) >= 0;
        if (quote)
        {
            sb.Append('"');
        }

        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
            }
            else
            {
                if (quote && ch == '"')
                {
                    // Double the backslashes before an embedded quote, then double the quote itself.
                    sb.Append('\\', backslashes);
                    sb.Append('"');
                }
                backslashes = 0;
            }

            if (ch == '%')
            {
                // %cd:~,% expands to an empty string, which splits the percent signs so they are never expanded.
                sb.Append("%%cd:~,%");
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (quote)
        {
            sb.Append('\\', backslashes);
            sb.Append('"');
        }
    }
}
