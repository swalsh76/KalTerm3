using KalTerm.Client;
using KalTerm.Contracts;

namespace KalTerm.TestSupport.Api;

/// <summary>
/// The base of every fake of the API in the tests. Each call refuses until a fake overrides it, so a method added to <see cref="IKalTermApi"/> is added here once and the fakes that need it override it;
/// the others keep compiling.
/// </summary>
public abstract class FakeKalTermApi : IKalTermApi
{
    /// <inheritdoc/>
    public virtual Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<MarketPage> GetMarketsAsync(MarketsQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<MarketDto> GetMarketAsync(string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<ScreenerPage> GetScreenerAsync(ScreenQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<WatchlistListDto> GetWatchlistsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<WatchlistDto> GetWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<WatchlistDto> CreateWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task DeleteWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task AddToWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task RemoveFromWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<AlertListDto> GetAlertsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<AlertDto> CreateAlertAsync(CreateAlertRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task SetAlertPausedAsync(int id, bool paused, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task DeleteAlertAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<AlertFiresPage> GetAlertFiresAsync(long? after = null, int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual IAsyncEnumerable<AlertStreamEvent> StreamAlertFiresAsync(long? after, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual IAsyncEnumerable<MarketStreamEvent> StreamMarketsAsync(MarketStreamRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task AddStreamMarketAsync(string streamId, StreamKind kind, string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task RemoveStreamMarketAsync(string streamId, StreamKind kind, string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<LayoutListDto> GetLayoutsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<LayoutDto> GetLayoutAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<LayoutDto> PutLayoutAsync(string name, System.Text.Json.JsonElement body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task DeleteLayoutAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<SettingDto> GetTimeZoneSettingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task SetTimeZoneSettingAsync(string? zone, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<MetricsDto> GetMetricsAsync(string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<TickPage> GetTicksAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<CandlePage> GetCandlesAsync(string ticker, string interval, WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<TradePage> GetTradesAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<OrderBookPage> GetOrderBooksAsync(int? depth = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<OrderBookDto> GetOrderBookAsync(string ticker, int? depth = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public virtual Task<GapPage> GetGapsAsync(WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

}
