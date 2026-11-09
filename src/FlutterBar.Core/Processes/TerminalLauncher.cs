namespace FlutterBar.Core.Processes;

/// <param name="Name">Shown in the Flutter flyout next to "Open terminal".</param>
public sealed record TerminalChoice(string Name, ProcessSpec Spec);

/// <summary>"Open terminal" in the project folder: Windows Terminal when installed, else Windows PowerShell.</summary>
public static class TerminalLauncher
{
    public const string WindowsTerminal = "Windows Terminal";
    public const string PowerShell = "PowerShell";

    public static TerminalChoice Resolve(IPathResolver paths, string projectRoot)
    {
        if (paths.Find("wt") is { } wt)
        {
            return new TerminalChoice(WindowsTerminal, new ProcessSpec(wt, ["-d", projectRoot], projectRoot));
        }
        // powershell.exe ships with Windows (System32 is on every PATH); the bare name is a last resort.
        var powershell = paths.Find("powershell") ?? "powershell.exe";
        return new TerminalChoice(PowerShell, new ProcessSpec(powershell, ["-NoExit"], projectRoot));
    }
}
