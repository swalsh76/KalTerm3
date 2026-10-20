using System.Windows;
using KalTerm.Terminal.Connect;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Views;

/// <summary>The real dialogs: modal windows over the main window.</summary>
/// <param name="owner">The window they belong to.</param>
internal sealed class WpfDialogs(Func<Window?> owner) : IDialogs
{
    public Task<Uri?> ShowConnectAsync(ConnectViewModel connect)
    {
        var window = new ConnectWindow { DataContext = connect, Owner = owner() };
        connect.Closed += window.Close;
        window.ShowDialog();
        connect.Closed -= window.Close;
        return Task.FromResult(connect.Result);
    }
}
