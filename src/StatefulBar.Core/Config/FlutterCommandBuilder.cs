namespace StatefulBar.Core.Config;

public static class FlutterCommandBuilder
{
    /// <summary>
    /// Builds the arguments after the flutter executable:
    /// run --machine -d &lt;deviceId&gt; -t &lt;program&gt; [--flavor &lt;flavor&gt;] --&lt;mode&gt; &lt;args...&gt;
    /// </summary>
    public static IReadOnlyList<string> BuildRunArgs(LaunchConfig config, string deviceId)
    {
        var args = new List<string> { "run", "--machine", "-d", deviceId, "-t", config.Program };
        if (!string.IsNullOrEmpty(config.Flavor))
        {
            args.Add("--flavor");
            args.Add(config.Flavor);
        }
        args.Add(config.Mode switch
        {
            RunMode.Debug => "--debug",
            RunMode.Profile => "--profile",
            RunMode.Release => "--release",
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Mode, "Unknown run mode"),
        });
        args.AddRange(config.Args);
        return args;
    }
}
