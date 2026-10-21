using KalTerm.Terminal.Mvvm;

namespace KalTerm.Terminal.Services;

/// <summary>A small dialog that asks for a text and says what is wrong with it while it is typed; OK is possible only when nothing is.</summary>
internal sealed class PromptViewModel : ObservableObject
{
    private readonly Func<string, string?> _check;
    private string _text = string.Empty;

    /// <summary>Creates the prompt.</summary>
    /// <param name="title">The window's title.</param>
    /// <param name="message">What the text is for.</param>
    /// <param name="check">What is wrong with the text so far, or <see langword="null"/>.</param>
    public PromptViewModel(string title, string message, Func<string, string?> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        Title = title;
        Message = message;
        _check = check;
        OkCommand = new RelayCommand(() => Close(Text), () => Problem is null);
        CancelCommand = new RelayCommand(() => Close(null));
    }

    /// <summary>The prompt was answered or given up on.</summary>
    public event Action? Closed;

    public string Title { get; }

    public string Message { get; }

    public RelayCommand OkCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>The answer, or <see langword="null"/> when the person gave up.</summary>
    public string? Result { get; private set; }

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value))
            {
                Raise(nameof(Problem));
                Raise(nameof(HasProblem));
                OkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What is wrong with the text, or <see langword="null"/>. Nothing is said before anything is typed.</summary>
    public string? Problem => _check(_text);

    /// <summary>Whether to show <see cref="Problem"/>: something has been typed and it will not do.</summary>
    public bool HasProblem => _text.Length > 0 && Problem is not null;

    private void Close(string? result)
    {
        Result = result;
        Closed?.Invoke();
    }
}
