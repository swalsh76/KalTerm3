-- Schema v8 (M7 slice 7.3b): the human-readable names. A market has Kalshi's own title ("Philadelphia wins"); an event has a title and a sub-title ("Philadelphia vs New York", "PHI vs NYK (Oct 20)").
-- Events live in their own table, keyed by the event ticker the markets already carry; there is no foreign key, because a market is stored before its event is known.
-- known = 1 when Kalshi returned the event; 0 when it said it has none (the empty row is remembered so it is not asked for again at once). fetched_at says when it was last asked.

ALTER TABLE dbo.market ADD title nvarchar(512) NOT NULL CONSTRAINT df_market_title DEFAULT (N'');
GO

CREATE TABLE dbo.market_event (
    event_ticker  varchar(128)  NOT NULL,
    series_ticker varchar(128)  NOT NULL CONSTRAINT df_market_event_series DEFAULT (''),
    title         nvarchar(512) NOT NULL CONSTRAINT df_market_event_title DEFAULT (N''),
    sub_title     nvarchar(512) NOT NULL CONSTRAINT df_market_event_sub_title DEFAULT (N''),
    known         bit           NOT NULL,
    fetched_at    datetime2(3)  NOT NULL,
    CONSTRAINT pk_market_event PRIMARY KEY CLUSTERED (event_ticker)
);
GO

-- The readers join a market to its event, and the backfill asks which events the markets mention that are not stored yet.
CREATE INDEX ix_market_event_ticker ON dbo.market (event_ticker);
GO
