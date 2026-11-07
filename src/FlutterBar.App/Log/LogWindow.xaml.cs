using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using FlutterBar.Core.Logging;

namespace FlutterBar.App.Log;

/// <summary>
/// Spec §8: an ordinary top-level window (own taskbar button, NOT owned by the widget) listing the
/// <see cref="LogBuffer"/>. Singleton: <see cref="ShowAt"/> opens it or brings the open one forward. Closing disposes
/// the instance and unsubscribes from the buffer; the run session is unaffected, and reopening rebuilds from the buffer.
/// It shares the UI thread with the taskbar-owned widget, so nothing here blocks: lines arrive in 100 ms batches.
/// </summary>
public partial class LogWindow : Window
{
    /// <summary>Scroll position within this many items of the end still counts as "at the bottom".</summary>
    private const double BottomTolerance = 2;

    private static LogBuffer? s_buffer;
    private static LogWindow? s_instance;

#if DEBUG
    private readonly LogBuffer _logBuffer;
#endif
    private readonly LogViewModel _vm;
    private ScrollViewer? _scroll;
    private bool _atBottom = true;
    private bool _scrollQueued;
    private ResetState? _reset;

    /// <summary>What to restore after the rows were rebuilt (rows are new objects: identity is the Seq of the line).</summary>
    private sealed record ResetState(long? FirstVisibleSeq, HashSet<long> SelectedSeqs);

    private LogWindow(LogBuffer buffer)
    {
#if DEBUG
        _logBuffer = buffer;
#endif
        InitializeComponent();
        _vm = new LogViewModel(buffer, Dispatcher);
        DataContext = _vm;
        _vm.RowsChanged += OnRowsChanged;
        _vm.Resetting += OnResetting;
        Loaded += OnLoaded;
        Closed += OnClosed;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopyToClipboard()));
        InputBindings.Add(new KeyBinding(new RelayKeyCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayKeyCommand(() => Navigate(forward: true)), Key.F3, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayKeyCommand(() => Navigate(forward: false)), Key.F3, ModifierKeys.Shift));
#if DEBUG
        PreviewKeyDown += OnDebugKeyDown;
#endif
    }

    /// <summary>Called once at startup with the buffer the run session writes to.</summary>
    public static void Configure(LogBuffer buffer) => s_buffer = buffer;

    /// <summary>
    /// Opens the window (or focuses the open one) and scrolls to <paramref name="seq"/>, expanding its exception block;
    /// null scrolls to the end. UI thread only.
    /// </summary>
    public static void ShowAt(long? seq)
    {
        var buffer = s_buffer ?? throw new InvalidOperationException("LogWindow.Configure was not called");
        var created = s_instance is null;
        var window = s_instance ??= new LogWindow(buffer);
        if (created)
        {
            window.Show();
        }
        else if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
        window.GoTo(seq);
    }

    /// <summary>Closes the window if it is open (app exit).</summary>
    public static void CloseIfOpen() => s_instance?.Close();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _scroll = FindScrollViewer(List);
        if (_scroll is not null)
        {
            _scroll.ScrollChanged += OnScrollChanged;
        }
        else
        {
            AppLog.Write("LogWindow: ScrollViewer not found, auto-scroll disabled");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_scroll is not null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
        }
        _vm.RowsChanged -= OnRowsChanged;
        _vm.Resetting -= OnResetting;
        _vm.Dispose();
        if (ReferenceEquals(s_instance, this))
        {
            s_instance = null;
        }
#if DEBUG
        if (DebugLogFiller.GcOnLogClose)
        {
            var weak = new WeakReference(this);
            var weakVm = new WeakReference(_vm);
            var weakList = new WeakReference(List);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => DebugLogFiller.ForceGcAndLog(weak, weakVm, weakList));
            var dispatcher = Dispatcher;
            _ = Task.Delay(4000).ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => DebugLogFiller.ForceGcAndLog(weak, weakVm, weakList)), TaskScheduler.Default);
        }
#endif
    }

    // ---------------------------------------------------------------- scrolling

    private bool IsAtBottom(ScrollViewer sv) => sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - BottomTolerance;

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        var now = IsAtBottom(sv);
        if (e.ExtentHeightChange == 0)
        {
            _atBottom = now; // the user scrolled (or the window was resized)
        }
        else if (now)
        {
            _atBottom = true; // content shrank until the end is in view
        }
        // else: content grew; stay "at bottom" if we were, and OnRowsChanged scrolls
    }

    // Rows are about to be replaced: remember the first visible line and the selection.
    private void OnResetting()
    {
        var rows = _vm.Rows;
        long? first = null;
        if (!_atBottom && _scroll is not null)
        {
            var index = (int)_scroll.VerticalOffset;
            if (index >= 0 && index < rows.Count)
            {
                first = rows[index].Line.Seq;
            }
        }
        var selected = new HashSet<long>();
        foreach (LogRow row in List.SelectedItems)
        {
            selected.Add(row.Line.Seq);
        }
        _reset = new ResetState(first, selected);
    }

    private void OnRowsChanged()
    {
        if (_reset is { } reset)
        {
            _reset = null;
            RestoreAfterReset(reset);
        }
        else if (!_atBottom && _scroll is not null && _vm.View.LastRemovedFromFront is var removed and > 0)
        {
            // Item-based scrolling: the offset is an item index, so rows evicted from the front shift the content up.
            _scroll.ScrollToVerticalOffset(Math.Max(0, _scroll.VerticalOffset - removed));
        }
        if (_atBottom)
        {
            ScrollToEnd();
        }
    }

    private void RestoreAfterReset(ResetState reset)
    {
        var rows = _vm.Rows;
        if (reset.SelectedSeqs.Count > 0)
        {
            foreach (var row in rows)
            {
                if (reset.SelectedSeqs.Contains(row.Line.Seq))
                {
                    List.SelectedItems.Add(row);
                }
            }
        }
        if (reset.FirstVisibleSeq is { } seq && !_atBottom)
        {
            var target = 0;
            while (target < rows.Count - 1 && rows[target].Line.Seq < seq)
            {
                target++; // rows are in sequence order: first row at or after the old top line
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _scroll?.ScrollToVerticalOffset(target));
        }
    }

    private void ScrollToEnd()
    {
        if (_scrollQueued)
        {
            return;
        }
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollQueued = false;
            var rows = _vm.Rows;
            if (rows.Count > 0)
            {
                List.ScrollIntoView(rows[^1]);
            }
        });
    }

    private void GoTo(long? seq)
    {
        if (seq is not { } target)
        {
            _atBottom = true;
            ScrollToEnd();
            return;
        }
        var view = _vm.View;
        var index = view.IndexOfSeq(target, expand: true);
        if (index < 0 && (view.ErrorsOnly || view.SearchText.Length > 0))
        {
            _vm.ResetFilters();
            index = view.IndexOfSeq(target, expand: true);
        }
        if (index < 0)
        {
            _atBottom = true; // the line was evicted from the buffer
            ScrollToEnd();
            return;
        }
        SelectAndReveal(view.Rows[index]);
    }

    private void SelectAndReveal(LogRow row)
    {
        _atBottom = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            List.SelectedItems.Clear();
            List.SelectedItem = row;
            List.ScrollIntoView(row);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found)
        {
            return found;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } inner)
            {
                return inner;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- toolbar

    private void OnPreviousErrorClick(object sender, RoutedEventArgs e) => Navigate(forward: false);

    private void OnNextErrorClick(object sender, RoutedEventArgs e) => Navigate(forward: true);

    private void Navigate(bool forward)
    {
        var view = _vm.View;
        int anchor;
        if (List.SelectedIndex >= 0)
        {
            anchor = List.SelectedIndex;
        }
        else
        {
            var top = _scroll is null ? 0 : (int)_scroll.VerticalOffset;
            anchor = forward ? top - 1 : top;
        }
        var index = view.FindError(anchor, forward);
        if (index < 0)
        {
            _vm.SetStatus("Tidak ada error");
            return;
        }
        _vm.SetStatus(null);
        SelectAndReveal(view.Rows[index]);
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _vm.Clear();

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyToClipboard();

    private void CopyToClipboard()
    {
        var lines = _vm.View.GetCopyLines(List.SelectedItems.Cast<LogRow>());
        if (lines.Count == 0)
        {
            return;
        }
        try
        {
            // Owned by this window (not NULL), bounded retry (about 100 ms).
            ClipboardText.Set(new System.Windows.Interop.WindowInteropHelper(this).Handle, LogView.FormatForCopy(lines));
            _vm.SetStatus($"{lines.Count:N0} baris disalin");
        }
        catch (System.ComponentModel.Win32Exception ex) // clipboard locked by another app, or not writable
        {
            AppLog.Write("LogWindow: copy failed", ex);
            _vm.SetStatus("Gagal menyalin: clipboard sedang dipakai aplikasi lain");
        }
    }

    // ---------------------------------------------------------------- groups

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is LogRow { IsHeader: true } row)
        {
            _vm.View.SetExpanded(row.Entry, !row.Entry.Expanded);
            e.Handled = true;
        }
    }

    // Toggles the row under the mouse; a double-click that started on the chevron button is its own toggle click.
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<Button>(source) is not null)
        {
            return;
        }
        if (FindAncestor<ListBoxItem>(source)?.DataContext is LogRow { IsHeader: true } row)
        {
            _vm.View.SetExpanded(row.Entry, !row.Entry.Expanded);
        }
    }

    private static T? FindAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        for (var node = start; node is not null;)
        {
            if (node is T found)
            {
                return found;
            }
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

#if DEBUG
    // Ctrl+Shift+F12: fill the buffer with 20,000 dummy lines (exception blocks included) to check scrolling and memory.
    private void OnDebugKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            DebugLogFiller.FillInBackground(_logBuffer, 20_000);
        }
        else if (e.Key == Key.F11 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            DebugLogFiller.StreamInBackground(_logBuffer, 20); // keeps appending: check scroll stability at capacity
        }
    }
#endif

    private sealed class RelayKeyCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
