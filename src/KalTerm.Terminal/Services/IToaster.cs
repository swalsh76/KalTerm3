namespace KalTerm.Terminal.Services;

/// <summary>What a toast says.</summary>
/// <param name="Title">The first line.</param>
/// <param name="Body">The second line.</param>
/// <param name="Ticker">The market a click should open; <see langword="null"/> for none.</param>
/// <param name="Silent">Whether the toast makes no sound.</param>
internal sealed record ToastMessage(string Title, string Body, string? Ticker, bool Silent);

/// <summary>Windows toast notifications, behind an interface so what the terminal says can be tested without a screen.</summary>
internal interface IToaster
{
    /// <summary>A toast was clicked; the market it named, if any. Raised on the UI thread, also when the click started the terminal.</summary>
    event Action<string?>? Activated;

    /// <summary>Shows a toast. It does not throw: a computer that cannot show toasts simply shows none.</summary>
    /// <param name="message">What it says.</param>
    void Show(ToastMessage message);
}
