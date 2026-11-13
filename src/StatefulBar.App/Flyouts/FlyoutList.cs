using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StatefulBar.Core.Orchestration;

namespace StatefulBar.App.Flyouts;

/// <summary>One row of a flyout list. <see cref="Value"/> carries the payload (path, branch, config name, device, emulator).</summary>
internal sealed class FlyoutItem
{
    public required string Title { get; init; }

    public string? Detail { get; init; }

    public string? ToolTip { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsHeader { get; init; }

    public bool IsCurrent { get; init; }

    public object? Value { get; init; }

    public bool IsChoosable => IsEnabled && !IsHeader;

    public static FlyoutItem Header(string title) => new() { Title = title, IsHeader = true };
}

/// <summary>
/// Keyboard and mouse behaviour shared by the flyout lists: type-to-filter in the search box (<see cref="SearchFilter"/>),
/// Up/Down move the selection over choosable rows, Enter picks the selected (or first) row, click picks a row.
/// Headers stay visible while their section has matches. Esc is handled by <see cref="FlyoutHost"/>.
/// </summary>
internal sealed class FlyoutList
{
    private readonly ListBox _list;
    private readonly TextBox? _search;
    private IReadOnlyList<FlyoutItem> _all = [];

    public FlyoutList(ListBox list, TextBox? search)
    {
        _list = list;
        _search = search;
        _list.PreviewKeyDown += OnKeyDown;
        _list.PreviewMouseLeftButtonUp += OnMouseUp;
        if (_search is not null)
        {
            _search.PreviewKeyDown += OnKeyDown;
            _search.TextChanged += (_, _) => ApplyFilter();
        }
    }

    /// <summary>A choosable row was picked (Enter or click).</summary>
    public event Action<FlyoutItem>? Chosen;

    public int VisibleCount => (_list.ItemsSource as IReadOnlyList<FlyoutItem>)?.Count(i => !i.IsHeader) ?? 0;

    public void SetItems(IReadOnlyList<FlyoutItem> items)
    {
        _all = items;
        ApplyFilter();
    }

    /// <summary>Focus target when the flyout opens: the search box, else the list.</summary>
    public IInputElement Focus => (IInputElement?)_search ?? _list;

    public void ClearSearch() => _search?.Clear();

    private void ApplyFilter()
    {
        var query = _search?.Text;
        var visible = new List<FlyoutItem>();
        FlyoutItem? pendingHeader = null;
        foreach (var item in _all)
        {
            if (item.IsHeader)
            {
                pendingHeader = item;
                continue;
            }
            if (!SearchFilter.Matches(item.Title + " " + item.Detail, query))
            {
                continue;
            }
            if (pendingHeader is not null)
            {
                visible.Add(pendingHeader);
                pendingHeader = null;
            }
            visible.Add(item);
        }
        _list.ItemsSource = visible;
        _list.SelectedItem = visible.FirstOrDefault(i => i.IsChoosable && i.IsCurrent) ?? visible.FirstOrDefault(i => i.IsChoosable);
        if (_list.SelectedItem is { } selected)
        {
            _list.ScrollIntoView(selected);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_list.SelectedItem is FlyoutItem { IsChoosable: true } item)
                {
                    Chosen?.Invoke(item);
                }
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_list.ItemsSource is not IReadOnlyList<FlyoutItem> items || items.Count == 0)
        {
            return;
        }
        var index = _list.SelectedItem is FlyoutItem current ? IndexOf(items, current) : -1;
        for (var i = index + delta; i >= 0 && i < items.Count; i += delta)
        {
            if (items[i].IsChoosable)
            {
                _list.SelectedItem = items[i];
                _list.ScrollIntoView(items[i]);
                return;
            }
        }
    }

    private static int IndexOf(IReadOnlyList<FlyoutItem> items, FlyoutItem item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }
        return -1;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem { DataContext: FlyoutItem { IsChoosable: true } item })
        {
            Chosen?.Invoke(item);
            e.Handled = true;
        }
    }
}
