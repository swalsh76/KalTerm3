using KalTerm.Terminal.Connect;

namespace KalTerm.Terminal.Services;

/// <summary>The few windows the view-models ask for. An interface so they can be tested without a screen.</summary>
internal interface IDialogs
{
    /// <summary>Shows the connect dialog and waits for it to close.</summary>
    /// <returns>The server that was connected, or <see langword="null"/> when the person gave up.</returns>
    Task<Uri?> ShowConnectAsync(ConnectViewModel connect);
}
