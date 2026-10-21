using System.Collections.ObjectModel;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Markets;

/// <summary>One line of the "Watchlists" menu: a list, checked when the market is in it (choosing it puts the market in the list or takes it out), or the line that makes a new list.</summary>
/// <param name="Text">What the line says.</param>
/// <param name="IsCheckable">Whether the line shows a check mark: the lists do, "New watchlist…" does not.</param>
/// <param name="IsMember">Whether the market is in the list.</param>
/// <param name="Command">What choosing the line does.</param>
internal sealed record WatchlistChoice(string Text, bool IsCheckable, bool IsMember, AsyncCommand Command);

/// <summary>
/// The menu that adds a market to a watchlist and removes it: one checked line for each list the person has, and "New watchlist…" to make one and put the market in it. It follows one market at a time
/// (<see cref="Ticker"/>) and redraws itself when the lists change, so the same menu serves the grid's selected row and the open detail.
/// </summary>
internal sealed class WatchlistMenu : ObservableObject, IDisposable
{
    private readonly WatchlistService _service;
    private readonly IDialogs? _dialogs;
    private readonly Action<string> _report;
    private string? _ticker;

    /// <summary>Creates the menu.</summary>
    /// <param name="service">The person's lists.</param>
    /// <param name="dialogs">Asks for the new list's name; without it "New watchlist…" does nothing.</param>
    /// <param name="report">Told a sentence when a change failed.</param>
    public WatchlistMenu(WatchlistService service, IDialogs? dialogs, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(report);
        _service = service;
        _dialogs = dialogs;
        _report = report;
        NewCommand = new AsyncCommand(NewAsync, ex => _report(ex.Message), () => _ticker is not null && _dialogs is not null);
        _service.Changed += Rebuild;
    }

    /// <summary>One line for each list.</summary>
    public ObservableCollection<WatchlistChoice> Choices { get; } = [];

    public AsyncCommand NewCommand { get; }

    /// <summary>The market the menu is for, or <see langword="null"/> when there is none.</summary>
    public string? Ticker
    {
        get => _ticker;
        set
        {
            if (Set(ref _ticker, value))
            {
                Rebuild();
                NewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void Dispose() => _service.Changed -= Rebuild;

    private void Rebuild()
    {
        Choices.Clear();
        if (_ticker is not { } ticker)
        {
            return;
        }

        foreach (var list in _service.Lists)
        {
            var name = list.Name;
            Choices.Add(new WatchlistChoice(name, IsCheckable: true, _service.IsMember(name, ticker), new AsyncCommand(() => ToggleAsync(name, ticker), ex => _report(ex.Message))));
        }

        Choices.Add(new WatchlistChoice("New watchlist…", IsCheckable: false, IsMember: false, NewCommand));
    }

    private async Task ToggleAsync(string name, string ticker)
    {
        var problem = await _service.SetMemberAsync(name, ticker, !_service.IsMember(name, ticker)).ConfigureAwait(true);
        if (problem is not null)
        {
            _report(problem);
        }
    }

    private async Task NewAsync()
    {
        if (_dialogs is null || _ticker is not { } ticker)
        {
            return;
        }

        var name = await _dialogs.AskNameAsync("New watchlist", $"A name for the watchlist that will hold {ticker}.", WatchlistService.NameProblem).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        var problem = await _service.CreateAsync(name).ConfigureAwait(true) ?? await _service.SetMemberAsync(name, ticker, member: true).ConfigureAwait(true);
        if (problem is not null)
        {
            _report(problem);
        }
    }
}
