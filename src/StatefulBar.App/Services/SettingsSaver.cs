using System.IO;
using System.Text.Json;
using StatefulBar.App.Diagnostics;
using StatefulBar.Core.Settings;

namespace StatefulBar.App.Services;

/// <summary>
/// Settings live on the UI thread. Saving takes a snapshot there (in-memory JSON round trip) and writes it atomically on a
/// background task, serialized; the UI thread never waits on file I/O (its input queue is attached to the taskbar's).
/// </summary>
internal sealed class SettingsSaver(AppSettingsStore store, AppSettings settings)
{
    private Task _chain = Task.CompletedTask;

    public AppSettings Settings { get; } = settings;

    /// <summary>Raised on a background thread with the user-facing message when a save failed (already logged).</summary>
    public event Action<string>? SaveFailed;

    /// <summary>Detached copy for background readers (scan, catalog build).</summary>
    public AppSettings Snapshot() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings)) ?? new AppSettings();

    public void Save(string what)
    {
        var copy = Snapshot();
        _chain = _chain.ContinueWith(_ =>
        {
            try
            {
                store.Save(copy);
                AppLog.Write($"settings saved ({what})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"settings save failed ({what})", ex);
                SaveFailed?.Invoke($"Couldn't save settings: {ex.Message}");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Completes when every queued save finished (exit).</summary>
    public Task FlushAsync() => _chain;
}
