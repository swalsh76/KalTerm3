using System.Windows;
using KalTerm.Terminal.Connect;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Trading;

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

    public Task<string?> AskNameAsync(string title, string message, Func<string, string?> check)
    {
        var prompt = new PromptViewModel(title, message, check);
        var window = new PromptWindow { DataContext = prompt, Owner = owner() };
        prompt.Closed += window.Close;
        window.ShowDialog();
        prompt.Closed -= window.Close;
        return Task.FromResult(prompt.Result);
    }

    public Task ShowOrderTicketAsync(OrderTicketViewModel ticket)
    {
        var window = new OrderTicketWindow { DataContext = ticket, Owner = owner() };
        ticket.Closed += window.Close;
        window.ShowDialog();
        ticket.Closed -= window.Close;
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(MessageBox.Show(owner(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
}
