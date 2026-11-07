using System.Windows;
using System.Windows.Controls;

namespace FlutterBar.App.Flyouts;

internal sealed partial class BranchFlyout : UserControl
{
    private readonly FlyoutList _list;

    public BranchFlyout()
    {
        InitializeComponent();
        _list = new FlyoutList(List, Search);
        _list.Chosen += item =>
        {
            if (item.Value is string branch)
            {
                BranchChosen?.Invoke(branch);
            }
        };
    }

    public event Action<string>? BranchChosen;

    public IInputElement FocusTarget => _list.Focus;

    public void Reset()
    {
        _list.ClearSearch();
        SetMessage(null);
    }

    public void SetBusy(bool busy) => Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    public void SetBranches(IReadOnlyList<string> branches, string? current) =>
        _list.SetItems([.. branches.Select(b => new FlyoutItem { Title = b, IsCurrent = b == current, Value = b })]);

    /// <summary>Inline problem text (e.g. git failed), or null to hide it.</summary>
    public void SetMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
