using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using Microsoft.Win32;

namespace FlutterBar.App.Theme;

/// <summary>
/// Light/dark brushes following the TASKBAR theme (<c>SystemUsesLightTheme</c>, spec §7.3), switched live through
/// <see cref="SystemEvents.UserPreferenceChanged"/>. Brushes go into a resource dictionary under the <c>Fb.*</c> keys
/// (used with DynamicResource). Dispose to unsubscribe the static SystemEvents handler.
/// </summary>
internal sealed class TaskbarTheme : IDisposable
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly ResourceDictionary _resources;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public TaskbarTheme(ResourceDictionary resources, Dispatcher dispatcher)
    {
        _resources = resources;
        _dispatcher = dispatcher;
        IsLight = ReadIsLight();
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsLight { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    // May arrive on the SystemEvents thread: marshal to the UI thread.
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            _dispatcher.BeginInvoke(Refresh);
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        var light = ReadIsLight();
        if (light == IsLight)
        {
            Apply(); // accent colour may have changed
            return;
        }
        IsLight = light;
        AppLog.Write($"theme -> {(light ? "light" : "dark")} (SystemUsesLightTheme)");
        Apply();
    }

    private static bool ReadIsLight()
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, "SystemUsesLightTheme", 0) is int value && value != 0;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("reading SystemUsesLightTheme failed, using dark", ex);
            return false;
        }
    }

    private void Apply()
    {
        var light = IsLight;
        Set("Fb.Background", light ? 0xFFEEEEEE : 0xFF202020);
        Set("Fb.Foreground", light ? 0xFF1B1B1B : 0xFFFFFFFF);
        Set("Fb.Muted", light ? 0xFF5F5F5F : 0xFFA8A8A8);
        Set("Fb.Chip", light ? 0xFFE0E0E0 : 0xFF2E2E2E);
        Set("Fb.Hover", light ? 0xFFD2D2D2 : 0xFF3D3D3D);
        Set("Fb.Separator", light ? 0xFFC8C8C8 : 0xFF404040);
        Set("Fb.Green", light ? 0xFF0F7B0F : 0xFF6CCB5F);
        Set("Fb.Yellow", light ? 0xFF9D5D00 : 0xFFFCE100);
        Set("Fb.Red", light ? 0xFFC42B1C : 0xFFFF6B6B);
        Set("Fb.Grey", light ? 0xFF8A8A8A : 0xFF8A8A8A);
        Set("Fb.Warning", light ? 0xFFBC5B00 : 0xFFF7A034);
        Set("Fb.Menu.Background", light ? 0xFFF9F9F9 : 0xFF2C2C2C);
        Set("Fb.Menu.Border", light ? 0xFFE5E5E5 : 0xFF3A3A3A);
        Set("Fb.Menu.Hover", light ? 0xFFEAEAEA : 0xFF3D3D3D);
        Set("Fb.Menu.Foreground", light ? 0xFF1A1A1A : 0xFFF3F3F3);
        Set("Fb.Menu.Disabled", light ? 0xFF9A9A9A : 0xFF7A7A7A);
        Set("Fb.Menu.Separator", light ? 0xFFE0E0E0 : 0xFF404040);
        _resources["Fb.Accent"] = SystemParameters.WindowGlassBrush is SolidColorBrush accent ? Frozen(accent.Color) : Frozen(Color.FromRgb(0x00, 0x78, 0xD4));
    }

    private void Set(string key, uint argb) =>
        _resources[key] = Frozen(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
