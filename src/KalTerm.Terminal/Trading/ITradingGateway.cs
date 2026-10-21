namespace KalTerm.Terminal.Trading;

/// <summary>
/// An open trading session with Kalshi, as the page and the order ticket use it: the account as it is, and the few things that can be done to it. The real one wraps <c>KalTerm.Trading</c>; tests give the page a fake, so nothing in a
/// test can send an order or touch a key. Trades go from this computer straight to Kalshi and never through the KalTerm server.
/// </summary>
internal interface ITradingLink : IAsyncDisposable
{
    /// <summary>Something the account view shows may have changed. May be raised on any thread.</summary>
    event Action? Changed;

    /// <summary>The account now.</summary>
    AccountView Account { get; }

    /// <summary>Looks up the market and asks the risk layer, without sending anything.</summary>
    /// <param name="entry">The order.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What would happen.</returns>
    Task<OrderPlan> PrepareAsync(OrderEntry entry, CancellationToken cancellationToken);

    /// <summary>The same for a change to a resting order.</summary>
    /// <param name="orderId">The resting order.</param>
    /// <param name="priceE6">The new YES price, or <see langword="null"/> to keep it.</param>
    /// <param name="countE2">The new total count, or <see langword="null"/> to keep it.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What would happen.</returns>
    Task<OrderPlan> PrepareAmendAsync(string orderId, long? priceE6, long? countE2, CancellationToken cancellationToken);

    /// <summary>Whether what was typed is the order, as the production confirmation asks: <c>TICKER buy|sell yes|no COUNT PRICE</c>.</summary>
    /// <param name="plan">The plan being confirmed.</param>
    /// <param name="typed">What the person typed.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    bool PhraseMatches(OrderPlan plan, string typed);

    /// <summary>Sends an approved plan: re-checks it with fresh data first and refuses when it changed.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What happened, as lines for the person.</returns>
    Task<IReadOnlyList<string>> SendAsync(OrderPlan plan, CancellationToken cancellationToken);

    /// <summary>Cancels a resting order. Never refused by the risk layer.</summary>
    /// <param name="orderId">The order.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What happened, as a line for the person.</returns>
    Task<string> CancelAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>The kill switch: halts new orders, cancels everything and checks until nothing rests.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What it did.</returns>
    Task<KillResult> KillAsync(CancellationToken cancellationToken);

    /// <summary>Clears the halt so new orders are allowed again.</summary>
    void Resume();
}

/// <summary>Opens trading sessions. The real one reads the trading key from Credential Manager and the environment from <c>KALSHI_ENV</c>, exactly as <c>kterm buy</c> does.</summary>
internal interface ITradingGateway
{
    /// <summary>Opens a session that can trade: the account is read and followed before it is handed out.</summary>
    /// <param name="cancellationToken">Cancels the opening.</param>
    /// <returns>The session; dispose it.</returns>
    Task<ITradingLink> OpenAsync(CancellationToken cancellationToken);

    /// <summary>Getting out without a session: opens the lightest one (no account stream, nothing to wait for), runs the kill switch and closes it.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What it did.</returns>
    Task<KillResult> KillAsync(CancellationToken cancellationToken);
}
