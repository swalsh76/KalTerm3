-- Schema v2 (M3 slice 3.7): the current order book of each watched market.
-- The ingest process builds the books in memory from Kalshi's order book stream and publishes each changed book here about once a second, so the API (a
-- separate process) can serve it. One row per market, overwritten in place: no history, and no deltas are stored (decision D6).
-- Levels are text, best price first, at most 100 per side: "price_e6:quantity_e2;price_e6:quantity_e2;..." (empty when there is no book yet).

CREATE TABLE dbo.orderbook_state (
    market_id      int           NOT NULL,
    updated_at     datetime2(3)  NOT NULL,   -- when the book last changed
    published_at   datetime2(3)  NOT NULL,   -- when the ingest last wrote this row (refreshed while the book is quiet, so a dead ingest shows)
    is_stale       bit           NOT NULL,   -- messages were missed (or there is no book yet) and recovery has not finished: do not trust the levels
    is_approximate bit           NOT NULL,   -- rebuilt from a REST snapshot, which cannot be aligned with the delta stream
    yes_levels     varchar(8000) NOT NULL CONSTRAINT df_orderbook_state_yes DEFAULT (''),
    no_levels      varchar(8000) NOT NULL CONSTRAINT df_orderbook_state_no DEFAULT (''),
    CONSTRAINT pk_orderbook_state PRIMARY KEY CLUSTERED (market_id)
);
GO
