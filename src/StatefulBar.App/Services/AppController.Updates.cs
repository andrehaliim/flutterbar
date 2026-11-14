using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using StatefulBar.App.Diagnostics;
using StatefulBar.Core.Integration;
using StatefulBar.Core.Orchestration;
using StatefulBar.Core.Updates;

namespace StatefulBar.App.Services;

/// <summary>
/// "Check for updates" (spec §7.4, <see cref="UpdateCheck"/>): once a day while the widget runs, and on demand from the
/// menu. It only tells the user about a newer release and opens its page; it never downloads or runs anything. Not used
/// in the Microsoft Store build. The request runs off the UI thread like every other I/O here.
/// </summary>
internal sealed partial class AppController
{
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdateDueRecheck = TimeSpan.FromHours(1);

    private bool _updateCheckRunning;

    private static Version CurrentVersion => typeof(AppController).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Updates ▸ Check now: always answers with a toast.</summary>
    public Task CheckForUpdatesFromMenuAsync() => CheckForUpdatesAsync(manual: true);

    /// <summary>
    /// The automatic check: the first one a minute after start (the widget should be up and docked first), then whenever
    /// a day has passed since the last answer. Looked at hourly, because a widget can run for weeks.
    /// </summary>
    private async Task UpdateLoopAsync()
    {
        if (PackageIdentity.IsPackaged)
        {
            AppLog.Write("update check: packaged, left to the Microsoft Store");
            return;
        }
        var delay = FirstUpdateCheckDelay;
        while (true)
        {
            await Task.Delay(delay, _lifetime.Token);
            delay = UpdateDueRecheck;
            if (Settings.Updates.Auto && UpdateCheck.IsDue(Settings.Updates.LastCheck, DateTimeOffset.Now))
            {
                await CheckForUpdatesAsync(manual: false);
            }
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckRunning)
        {
            if (manual)
            {
                ShowToast(UiText.UpdateCheckRunning);
            }
            return;
        }
        _updateCheckRunning = true;
        try
        {
            var current = CurrentVersion;
            UpdateResult result;
            try
            {
                var json = await Task.Run(() => FetchLatestReleaseAsync(current, _lifetime.Token));
                result = UpdateCheck.Evaluate(json, current);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw; // exit
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
            {
                // Offline, a proxy, GitHub's rate limit, or the 10 s timeout (OperationCanceledException).
                var reason = ex is OperationCanceledException ? "GitHub didn't answer in time" : ex.Message;
                result = new UpdateResult(UpdateStatus.Failed, null, null, reason);
            }
            AppLog.Write($"update check ({(manual ? "menu" : "automatic")}): {result.Status} current={UpdateCheck.Three(current)} latest={(result.Latest is null ? "-" : UpdateCheck.Three(result.Latest))}{(result.Error is null ? string.Empty : " error=" + result.Error)}");
            ApplyUpdateResult(result, current, manual);
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private void ApplyUpdateResult(UpdateResult result, Version current, bool manual)
    {
        if (_disposed)
        {
            return;
        }
        if (result.Status == UpdateStatus.Failed)
        {
            // The automatic check stays quiet and tries again within the hour: LastCheck is not moved.
            if (manual)
            {
                ShowToast(UiText.UpdateCheckFailed(result.Error ?? "unknown error"));
            }
            return;
        }

        if (result is { Status: UpdateStatus.Available, Latest: { } latest, ReleasePage: { } page })
        {
            // A toast cannot be shown while the docking hides the widget (fullscreen app, auto-hide).
            var visible = CanShowToast;
            if (!manual && UpdateCheck.ShouldNotify(latest, Settings.Updates.NotifiedVersion) && !visible)
            {
                // Keep the check due (LastCheck is not moved): it runs again within the hour, until the user can be told.
                AppLog.Write($"update check: widget hidden, telling about {UpdateCheck.Three(latest)} is postponed");
                return;
            }
            if (manual || UpdateCheck.ShouldNotify(latest, Settings.Updates.NotifiedVersion))
            {
                if (visible)
                {
                    Settings.Updates.NotifiedVersion = UpdateCheck.Three(latest); // told once; only when it was really shown
                }
                ShowToast(UiText.UpdateAvailable(latest), UiText.ViewRelease, () => OpenReleasePageAsync(page));
            }
        }
        else if (manual)
        {
            ShowToast(UiText.UpToDate(current));
        }
        Settings.Updates.LastCheck = DateTimeOffset.Now;
        _saver.Save("update check");
    }

    /// <summary>One GET to <see cref="UpdateCheck.LatestReleaseApi"/>; the client lives for this request only (one a day).</summary>
    private static async Task<string> FetchLatestReleaseAsync(Version current, CancellationToken ct)
    {
        using var http = new HttpClient
        {
            Timeout = UpdateCheck.RequestTimeout,
            MaxResponseContentBufferSize = UpdateCheck.MaxResponseBytes,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateCheck.UserAgent(current));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync(UpdateCheck.LatestReleaseApi, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task OpenReleasePageAsync(string page)
    {
        try
        {
            await Task.Run(() => Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose());
            AppLog.Write($"update check: opened {page}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"opening {page} failed", ex);
            ShowToast($"Couldn't open {page}: {ex.Message}");
        }
    }
}
