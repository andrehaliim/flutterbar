namespace FlutterBar.Core.Logging;

/// <summary>Recognises the framing lines Flutter prints around uncaught exceptions.</summary>
public static class LogClassifier
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
        var span = text.AsSpan().Trim();
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
        var span = text.AsSpan().Trim();
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
}
