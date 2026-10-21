using KalTerm.Terminal.Connect;
using KalTerm.Terminal.Trading;

namespace KalTerm.Terminal.Services;

/// <summary>The few windows the view-models ask for. An interface so they can be tested without a screen.</summary>
internal interface IDialogs
{
    /// <summary>Shows the connect dialog and waits for it to close.</summary>
    /// <returns>The server that was connected, or <see langword="null"/> when the person gave up.</returns>
    Task<Uri?> ShowConnectAsync(ConnectViewModel connect);

    /// <summary>Asks for a short text, checking it as it is typed.</summary>
    /// <param name="title">The window's title.</param>
    /// <param name="message">What the text is for.</param>
    /// <param name="check">Returns what is wrong with the text so far, or <see langword="null"/> when it will do.</param>
    /// <returns>The text, or <see langword="null"/> when the person gave up.</returns>
    Task<string?> AskNameAsync(string title, string message, Func<string, string?> check);

    /// <summary>Shows the order ticket and waits for it to be closed.</summary>
    /// <param name="ticket">The ticket.</param>
    /// <returns>A task that completes when the ticket is closed.</returns>
    Task ShowOrderTicketAsync(OrderTicketViewModel ticket);

    /// <summary>Asks a yes-or-no question.</summary>
    /// <param name="title">The window's title.</param>
    /// <param name="message">The question.</param>
    /// <returns><see langword="true"/> when the person agreed.</returns>
    Task<bool> ConfirmAsync(string title, string message);
}
