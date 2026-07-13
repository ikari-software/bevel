using System.Windows.Input;

namespace Bevel.Taskbar;

/// <summary>
/// Minimal <see cref="ICommand"/> for async handlers, so DataTemplates can bind
/// <c>Button.Command</c> declaratively instead of the view wiring click handlers to
/// hand-built controls. Re-entrancy is guarded — the command reports CanExecute=false while
/// its handler runs, so a double-click can't fire an activate/launch twice.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _execute(); }
        catch (Exception ex) { TaskbarLog.Swallowed("AsyncRelayCommand", ex); } // never crash the shell
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
