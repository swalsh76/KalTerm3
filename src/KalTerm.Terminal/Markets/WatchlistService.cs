using System.Text.RegularExpressions;
using KalTerm.Client;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Markets;

/// <summary>A watchlist as the person sees it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Count">How many markets it holds.</param>
internal sealed record WatchlistInfo(string Name, int Count);

/// <summary>
/// The person's watchlists, kept in memory: every list and the markets in it, read once when the page attaches and updated by each change made here. A change is sent to the server first and applied here only when
/// the server accepted it. Every method returns a sentence for the person when it failed, <see langword="null"/> when it worked, so a view-model can show it as it is.
/// </summary>
internal sealed partial class WatchlistService
{
    /// <summary>The most lists the server allows one person.</summary>
    public const int MaxLists = 20;

    /// <summary>The most markets the server allows in one list.</summary>
    public const int MaxMarkets = 500;

    private readonly SortedDictionary<string, HashSet<string>> _lists = new(StringComparer.Ordinal);
    private IKalTermApi? _api;

    /// <summary>The lists or their markets changed.</summary>
    public event Action? Changed;

    /// <summary>The lists, by name.</summary>
    public IReadOnlyList<WatchlistInfo> Lists => [.. _lists.Select(l => new WatchlistInfo(l.Key, l.Value.Count))];

    /// <summary>Whether a list has been read since the last <see cref="Attach"/>.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Points the service at a server (or at none) and forgets what it held.</summary>
    /// <param name="api">The client for the person's own requests, or <see langword="null"/> to detach.</param>
    public void Attach(IKalTermApi? api)
    {
        _api = api;
        Loaded = false;
        if (_lists.Count > 0)
        {
            _lists.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>What is wrong with a name, or <see langword="null"/> when it will be accepted.</summary>
    /// <param name="name">The name typed.</param>
    /// <returns>A sentence, or <see langword="null"/>.</returns>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Type a name.";
        }

        if (name.Length > 64)
        {
            return "At most 64 characters.";
        }

        return NameShape().IsMatch(name) ? null : "Lower-case letters, digits, dots, dashes and underscores only, starting with a letter or digit.";
    }

    /// <summary>The names of the lists a market is in.</summary>
    /// <param name="ticker">The market.</param>
    /// <returns>The names, in order.</returns>
    public IReadOnlyList<string> ListsOf(string ticker) => [.. _lists.Where(l => l.Value.Contains(ticker)).Select(l => l.Key)];

    /// <summary>Whether a market is in a list.</summary>
    /// <param name="name">The list.</param>
    /// <param name="ticker">The market.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool IsMember(string name, string ticker) => _lists.TryGetValue(name, out var set) && set.Contains(ticker);

    /// <summary>Reads every list and its markets again.</summary>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public async Task<string?> LoadAsync()
    {
        if (_api is not { } api)
        {
            return null;
        }

        try
        {
            var summary = await api.GetWatchlistsAsync().ConfigureAwait(true);
            var read = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var list in summary.Watchlists)
            {
                var full = await api.GetWatchlistAsync(list.Name).ConfigureAwait(true);
                read[list.Name] = new HashSet<string>(full.Markets, StringComparer.Ordinal);
            }

            _lists.Clear();
            foreach (var (name, set) in read)
            {
                _lists[name] = set;
            }

            Loaded = true;
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return ApiProblem.Say(ex, "read your watchlists");
        }
    }

    /// <summary>Makes a list.</summary>
    /// <param name="name">Its name; checked with <see cref="NameProblem"/> first.</param>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public async Task<string?> CreateAsync(string name)
    {
        if (NameProblem(name) is { } problem)
        {
            return problem;
        }

        if (_lists.ContainsKey(name))
        {
            return $"You already have a watchlist called {name}.";
        }

        return await RunAsync(
            (api, token) => api.CreateWatchlistAsync(name, token),
            () => _lists[name] = new HashSet<string>(StringComparer.Ordinal),
            "make the watchlist",
            $"You already have {MaxLists} watchlists. Delete one first.",
            $"You already have a watchlist called {name}.").ConfigureAwait(true);
    }

    /// <summary>Deletes a list (the markets themselves are untouched).</summary>
    /// <param name="name">The list.</param>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public Task<string?> DeleteAsync(string name) => RunAsync(
        (api, token) => api.DeleteWatchlistAsync(name, token),
        () => _lists.Remove(name),
        "delete the watchlist",
        null,
        null);

    /// <summary>Puts a market in a list or takes it out.</summary>
    /// <param name="name">The list.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="member"><see langword="true"/> to put it in, <see langword="false"/> to take it out.</param>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    public Task<string?> SetMemberAsync(string name, string ticker, bool member) => RunAsync(
        (api, token) => member ? api.AddToWatchlistAsync(name, ticker, token) : api.RemoveFromWatchlistAsync(name, ticker, token),
        () =>
        {
            if (_lists.TryGetValue(name, out var set))
            {
                _ = member ? set.Add(ticker) : set.Remove(ticker);
            }
        },
        member ? "add the market" : "remove the market",
        $"The watchlist {name} is full ({MaxMarkets} markets).",
        null);

    private async Task<string?> RunAsync(Func<IKalTermApi, CancellationToken, Task> send, Action apply, string doing, string? limitText, string? existsText)
    {
        if (_api is not { } api)
        {
            return "Not connected.";
        }

        try
        {
            await send(api, CancellationToken.None).ConfigureAwait(true);
        }
        catch (ApiException ex) when (ex.Code == "limit_reached" && limitText is not null)
        {
            return limitText;
        }
        catch (ApiException ex) when (ex.Code == "already_exists" && existsText is not null)
        {
            return existsText;
        }
        catch (ApiException ex) when (ex.Code == "not_found")
        {
            return "That watchlist no longer exists. Use Refresh on the list box to read your lists again.";
        }
        catch (ApiException ex) when (ex.Code == "market_not_found")
        {
            return "The server does not know that market.";
        }
        catch (Exception ex) when (ApiProblem.IsExpected(ex))
        {
            return ApiProblem.Say(ex, doing);
        }

        apply();
        Changed?.Invoke();
        return null;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameShape();
}
