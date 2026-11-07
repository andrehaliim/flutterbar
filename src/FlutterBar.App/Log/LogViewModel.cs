using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FlutterBar.Core.Logging;

namespace FlutterBar.App.Log;

/// <summary>
/// Drives the LogWindow list. <see cref="LogBuffer.Appended"/> is only a wake-up signal (it can fire out of sequence
/// order under concurrency): a 100 ms batch tick then reads <c>Since(LastSeq)</c>, which is ordered, and feeds
/// <see cref="LogView"/>. Everything except the <see cref="LogBuffer.Appended"/> handler runs on the UI thread.
/// </summary>
internal sealed class LogViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(200);

    private readonly LogBuffer _buffer;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _batchTimer;
    private readonly DispatcherTimer _searchTimer;
    private int _scheduled;
    private bool _disposed;
    private string _searchText = string.Empty;
    private bool _errorsOnly;
    private string? _transientStatus;

    public LogViewModel(LogBuffer buffer, Dispatcher dispatcher)
    {
        _buffer = buffer;
        _dispatcher = dispatcher;
        View = new LogView();
        View.RowsReset += OnRowsReset;
        _batchTimer = new DispatcherTimer(BatchInterval, DispatcherPriority.Background, OnBatchTick, dispatcher);
        _batchTimer.Stop();
        _searchTimer = new DispatcherTimer(SearchDebounce, DispatcherPriority.Normal, OnSearchTick, dispatcher);
        _searchTimer.Stop();

        // Subscribe first, then read: a line appended in between is picked up by the next Since(LastSeq).
        _buffer.Appended += OnAppended;
        View.Append(_buffer.Snapshot());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Rows were added, removed or replaced: the window keeps the list pinned to the bottom when it was there.</summary>
    public event Action? RowsChanged;

    public LogView View { get; }

    public ObservableCollection<LogRow> Rows => View.Rows;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                _searchTimer.Stop();
                _searchTimer.Start(); // debounce: the filter runs 200 ms after the last keystroke
            }
        }
    }

    public bool ErrorsOnly
    {
        get => _errorsOnly;
        set
        {
            if (Set(ref _errorsOnly, value))
            {
                View.SetErrorsOnly(value);
            }
        }
    }

    public string StatusText
    {
        get
        {
            if (_transientStatus is not null)
            {
                return _transientStatus;
            }
            var shown = View.Rows.Count;
            var total = View.TotalLines;
            var filtered = View.SearchText.Length > 0 || View.ErrorsOnly;
            return filtered
                ? string.Create(CultureInfo.CurrentCulture, $"{shown:N0} baris tampil, {total:N0} total")
                : string.Create(CultureInfo.CurrentCulture, $"{total:N0} baris");
        }
    }

    public void SetStatus(string? message)
    {
        _transientStatus = message;
        OnPropertyChanged(nameof(StatusText));
    }

    public void Clear()
    {
        View.Clear();
        SetStatus(null);
    }

    /// <summary>Drops both filters immediately (used when a requested line is hidden by them).</summary>
    public void ResetFilters()
    {
        _searchTimer.Stop();
        Set(ref _searchText, string.Empty, nameof(SearchText));
        Set(ref _errorsOnly, false, nameof(ErrorsOnly));
        View.SetSearch(string.Empty);
        View.SetErrorsOnly(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _buffer.Appended -= OnAppended;
        View.RowsReset -= OnRowsReset;
        _batchTimer.Stop();
        _searchTimer.Stop();
    }

    // Any thread, possibly out of order, must not throw: only schedule one batch tick.
    private void OnAppended(LogLine ignored)
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    _batchTimer.Start();
                }
            });
        }
    }

    private void OnBatchTick(object? sender, EventArgs e)
    {
        _batchTimer.Stop();
        Interlocked.Exchange(ref _scheduled, 0); // before reading: a line appended from now on schedules the next tick
        if (_disposed)
        {
            return;
        }
        var lines = _buffer.Since(View.LastSeq);
        if (lines.Count == 0)
        {
            return;
        }
        View.Append(lines);
        _transientStatus = null;
        OnPropertyChanged(nameof(StatusText));
        RowsChanged?.Invoke();
    }

    private void OnSearchTick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        if (!_disposed)
        {
            View.SetSearch(_searchText);
        }
    }

    private void OnRowsReset()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(StatusText));
        RowsChanged?.Invoke();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
