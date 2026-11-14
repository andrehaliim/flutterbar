namespace StatefulBar.Core.Orchestration;

/// <summary>English UI copy shared by the widget, flyouts and toasts (spec §5, §7, §10).</summary>
public static class UiText
{
    public const string PickProject = "Select project";
    public const string PickConfig = "Select config";
    public const string PickDevice = "Select device";
    public const string NoConfig = "No config yet";
    public const string ConfigInvalid = "Invalid config";
    public const string ImportFromIde = "Import from IDE";
    public const string CreateDefault = "Create default";
    public const string EditFile = "Edit file";
    public const string Connected = "Connected";
    public const string OfflineEmulators = "Emulator (offline)";
    public const string EmulatorTimeout = "Emulator isn't ready after 2 minutes";
    public const string ConfirmSwitch = "Stop the current session and switch?";
    public const string ConfirmYes = "Stop";
    public const string ConfirmNo = "Cancel";
    public const string BranchHotRestart = "Branch changed. Hot restart?";
    public const string HotRestart = "Hot restart";
    public const string PubspecChanged = "pubspec changed. Stop and run again.";
    public const string PulledHotRestart = "Pulled. Hot restart?";
    public const string PulledPubspecChanged = "Pulled. pubspec changed. Stop and run again.";
    public const string NoCommitYet = "(no commits yet)";
    public const string ConfirmExit = "Stop the session and exit?";
    public const string ConfirmExitYes = "Exit";
    public const string UnexpectedError = "StatefulBar hit an error. Details are in diag.log";
    public const string PathAdded = "Open a new terminal to pick up the PATH change";
    public const string NeedProject = "Select a project first";
    public const string StartupToggle = "Start with Windows";
    public const string StartupSettings = "Start with Windows…";
    public const string StartupSettingsToolTip = "Opens the Windows startup settings, where StatefulBar is turned on or off";
    public const string ViewRelease = "View release";
    public const string UpdateCheckRunning = "Already checking for updates";

    public static string UpdateAvailable(Version latest) => $"StatefulBar {Updates.UpdateCheck.Three(latest)} is available";

    public static string UpToDate(Version current) => $"You're on the latest version ({Updates.UpdateCheck.Three(current)})";

    public static string UpdateCheckFailed(string reason) => $"Couldn't check for updates: {reason}";

    /// <summary>
    /// At most <paramref name="maxLines"/> non-empty lines of git output for a toast or inline message, with an ellipsis line
    /// when text was cut (the full text goes to diag.log).
    /// </summary>
    public static string CapLines(string text, int maxLines = 6)
    {
        var lines = text.Split(Newline, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= maxLines ? string.Join(Newline, lines) : string.Join(Newline, lines.Take(maxLines)) + Newline + "…";
    }

    private const char Newline = '\n';
}
