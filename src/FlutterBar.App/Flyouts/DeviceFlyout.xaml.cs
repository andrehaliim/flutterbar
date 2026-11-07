using System.Windows;
using System.Windows.Controls;
using FlutterBar.Core.Devices;
using FlutterBar.Core.Orchestration;

namespace FlutterBar.App.Flyouts;

internal sealed partial class DeviceFlyout : UserControl
{
    private readonly FlyoutList _list;

    public DeviceFlyout()
    {
        InitializeComponent();
        _list = new FlyoutList(List, null);
        _list.Chosen += item =>
        {
            switch (item.Value)
            {
                case DeviceInfo device:
                    DeviceChosen?.Invoke(device);
                    break;
                case EmulatorInfo emulator:
                    EmulatorChosen?.Invoke(emulator);
                    break;
            }
        };
    }

    public event Action<DeviceInfo>? DeviceChosen;

    /// <summary>An offline emulator was picked: launch it.</summary>
    public event Action<EmulatorInfo>? EmulatorChosen;

    public IInputElement FocusTarget => List;

    /// <summary>Warning line at the top (why the flyout was opened for the user), or null to hide it.</summary>
    public void SetNotice(string? text)
    {
        Notice.Text = text ?? "";
        Notice.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetBusy(bool busy) => Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    public void SetDevices(IReadOnlyList<DeviceInfo> devices, IReadOnlyList<EmulatorInfo> offline, string? currentId, bool refreshing)
    {
        var items = new List<FlyoutItem> { FlyoutItem.Header(UiText.Connected) };
        items.AddRange(devices.Select(d => new FlyoutItem
        {
            Title = d.Name,
            Detail = DevicePresentation.Detail(d),
            IsCurrent = string.Equals(d.Id, currentId, StringComparison.OrdinalIgnoreCase),
            Value = d,
        }));
        if (devices.Count == 0)
        {
            items.Add(new FlyoutItem { Title = refreshing ? "Loading…" : "No devices connected", IsEnabled = false });
        }
        if (offline.Count > 0)
        {
            items.Add(FlyoutItem.Header(UiText.OfflineEmulators));
            items.AddRange(offline.Select(e => new FlyoutItem { Title = e.Name, Detail = e.Id, ToolTip = "Start emulator", Value = e }));
        }
        _list.SetItems(items);
    }

    public void SetMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
