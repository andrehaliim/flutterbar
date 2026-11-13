using System.Text.RegularExpressions;

namespace StatefulBar.Core.Logging;

/// <summary>
/// Recognises the framing lines Flutter prints around uncaught exceptions. Device log prefixes are ignored: Android
/// forwards logcat lines as <c>I/flutter ( 1234): …</c> (flutter_tools AdbLogReader), iOS as <c>flutter: …</c>.
/// </summary>
public static partial class LogClassifier
{
    private const char Bar = '═';
    private const char HeaderCap = '╡';
    private const string HeaderPhrase = "exception caught by";
    private const int MinFooterLength = 20;

    /// <summary>
    /// True for both device style (<c>════════ Exception caught by widgets library ═══</c>) and
    /// web style (<c>══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══</c>) headers.
    /// </summary>
    public static bool IsExceptionHeader(string text)
    {
        var span = WithoutDevicePrefix(text).Trim();
        var bars = 0;
        while (bars < span.Length && span[bars] == Bar)
        {
            bars++;
        }
        if (bars == 0)
        {
            return false;
        }
        span = span[bars..];
        if (span.Length > 0 && span[0] == HeaderCap)
        {
            span = span[1..];
        }
        return span.TrimStart().StartsWith(HeaderPhrase, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the whole trimmed line is at least 20 box-drawing bars.</summary>
    public static bool IsExceptionFooter(string text)
    {
        var span = WithoutDevicePrefix(text).Trim();
        if (span.Length < MinFooterLength)
        {
            return false;
        }
        foreach (var c in span)
        {
            if (c != Bar)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <paramref name="text"/> without a leading <c>I/flutter ( 1234): </c> (any of V/D/I/W/E/F, pid optional) or
    /// <c>flutter: </c> prefix; used for classification only, the log keeps the original text.
    /// </summary>
    internal static ReadOnlySpan<char> WithoutDevicePrefix(string text)
    {
        var match = DevicePrefix().Match(text);
        return match.Success ? text.AsSpan(match.Length) : text.AsSpan();
    }

    [GeneratedRegex(@"^\s*(?:[VDIWEF]/flutter\s*(?:\(\s*\d+\s*\))?:|flutter:)\s?", RegexOptions.CultureInvariant)]
    private static partial Regex DevicePrefix();
}
