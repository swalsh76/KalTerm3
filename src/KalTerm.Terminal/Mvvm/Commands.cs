using System.Windows.Input;

namespace KalTerm.Terminal.Mvvm;

/// <summary>A command that runs a method on the UI thread.</summary>
/// <param name="execute">What it does.</param>
/// <param name="canExecute">Whether it may run now; always when omitted.</param>
internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A command that runs an async method. It cannot run again while it is running, and a failure is handed to <paramref name="onError"/>
/// instead of being lost (an exception thrown from an async void handler would otherwise end the program).
/// </summary>
/// <param name="execute">What it does.</param>
/// <param name="onError">Told about a failure; the message goes to the person.</param>
/// <param name="canExecute">Whether it may run now; always when omitted.</param>
internal sealed class AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await RunAsync().ConfigureAwait(true);

    /// <summary>Runs the command and returns when it finishes (tests await this; the view calls <see cref="Execute"/>).</summary>
    public async Task RunAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // cancelled on purpose: not a failure to report
        }
        catch (Exception ex)
        {
            onError(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
