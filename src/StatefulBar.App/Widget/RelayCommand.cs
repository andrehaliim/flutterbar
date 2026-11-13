using System.Windows.Input;

namespace StatefulBar.App.Widget;

/// <summary>Minimal <see cref="ICommand"/>; <see cref="Refresh"/> re-queries <c>CanExecute</c>.</summary>
internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
