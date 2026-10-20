-- Schema v5 (M4 slice 4.4): candles, the long-term record that outlives the raw ticker and trade rows the storage governor drops.
-- One row per (interval, market, bucket) that had a trade or a ticker update. Buckets are UTC, aligned to the epoch (a week to Monday). Money and counts are fixed-point as everywhere:
-- _e6 dollars, _e2 contracts. The price (open/high/low/close, yes side) comes from trades, so a bucket with ticks but no trades has a null price; bid, ask and open interest are the last
-- ticker values of the bucket. A bucket that overlaps a gap is flagged: trades_partial until the gap's trade backfill is done (or skipped), book_partial for good.

ALTER TABLE dbo.ingest_gap ADD candles_built_at datetime2(3) NULL;   -- when the candles over this gap were last rebuilt after its trade backfill finished
GO

CREATE TABLE dbo.candle (
    interval_s       int           NOT NULL,   -- 60, 300, 900, 3600, 14400 or 86400
    market_id        int           NOT NULL,
    bucket_start     datetime2(0)  NOT NULL,
    open_e6          bigint        NULL,
    high_e6          bigint        NULL,
    low_e6           bigint        NULL,
    close_e6         bigint        NULL,
    volume_e2        bigint        NOT NULL,
    trade_count      int           NOT NULL,
    yes_bid_e6       bigint        NULL,       -- at the last ticker update of the bucket
    yes_ask_e6       bigint        NULL,
    open_interest_e2 bigint        NULL,
    trades_partial   bit           NOT NULL,
    book_partial     bit           NOT NULL,
    gap_kind         varchar(32)   NULL,       -- the kind of the gap that overlaps the bucket most
    gap_seconds      int           NULL,       -- seconds of the bucket covered by gaps
    built_at         datetime2(3)  NOT NULL,
    CONSTRAINT pk_candle PRIMARY KEY CLUSTERED (interval_s, market_id, bucket_start) WITH (DATA_COMPRESSION = PAGE)
);
GO
