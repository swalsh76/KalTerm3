-- Schema v6 (M5 slice 5.2): the analyzer runner's records and the first analyzer's result table.
-- market_metrics holds one row per market that had a ticker update or a trade in the last 24 hours, refreshed by the market_metrics analyzer; a market idle for 24 hours loses its row.
-- Money and counts are fixed-point as everywhere: _e6 dollars, _e2 contracts. A figure without enough data is NULL (see MetricsCalculator), never a stand-in.
-- analyzer_run is the history of runs (kept for a week) so a stalled or failing analyzer can be seen.

CREATE TABLE dbo.market_metrics (
    market_id        int           NOT NULL,
    changed_at       datetime2(3)  NOT NULL,   -- when these figures were last found to differ from the row before; how fresh the table is comes from analyzer_run
    last_time        datetime2(3)  NULL,       -- the latest ticker update used (within the last hour)
    last_e6          bigint        NULL,
    change_24h_e6    bigint        NULL,
    volume_e2        bigint        NOT NULL,
    trade_count      bigint        NOT NULL,
    open_interest_e2 bigint        NULL,
    yes_bid_e6       bigint        NULL,
    yes_ask_e6       bigint        NULL,
    spread_e6        bigint        NULL,
    vwap_e6          bigint        NULL,
    sma20_e6         bigint        NULL,
    sma50_e6         bigint        NULL,
    volatility_e6    bigint        NULL,
    is_partial       bit           NOT NULL,
    CONSTRAINT pk_market_metrics PRIMARY KEY CLUSTERED (market_id)
);
GO
CREATE INDEX ix_market_metrics_volume ON dbo.market_metrics (volume_e2 DESC);
GO

CREATE TABLE dbo.analyzer_run (
    run_id      bigint IDENTITY(1,1) NOT NULL,
    name        varchar(64)   NOT NULL,
    started_at  datetime2(3)  NOT NULL,
    finished_at datetime2(3)  NULL,            -- NULL while it runs (or if the process stopped in the middle)
    status      varchar(16)   NOT NULL,        -- running, ok, failed, timeout
    row_count   int           NULL,
    changed     bit           NULL,
    message     nvarchar(400) NULL,
    CONSTRAINT pk_analyzer_run PRIMARY KEY CLUSTERED (run_id)
);
GO
CREATE INDEX ix_analyzer_run_name_started ON dbo.analyzer_run (name, started_at DESC);
GO
