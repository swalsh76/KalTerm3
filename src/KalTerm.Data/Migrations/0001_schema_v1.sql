-- Schema v1 (M3). Money columns are bigint fixed-point: *_e6 = dollars at 10^-6, *_e2 = contracts at 10^-2.
-- Time-series tables (ticker, trade) are daily-partitioned (UTC days) with an ordered clustered columnstore
-- (BUILD-LOG 2.4, layout A). They carry no foreign keys and no unique constraints; de-duplication is the writer's job.
-- Boundaries are added ahead of need by PartitionManager; the function starts with a single partition.

CREATE PARTITION FUNCTION pf_day (datetime2(3)) AS RANGE RIGHT FOR VALUES ();
GO
CREATE PARTITION SCHEME ps_day AS PARTITION pf_day ALL TO ([PRIMARY]);
GO

CREATE TABLE dbo.market (
    market_id             int IDENTITY(1,1) NOT NULL,
    ticker                varchar(128)  NOT NULL,
    event_ticker          varchar(128)  NOT NULL,
    market_type           varchar(32)   NOT NULL,
    status                varchar(32)   NOT NULL,
    yes_sub_title         nvarchar(256) NOT NULL CONSTRAINT df_market_yes_sub_title DEFAULT (N''),
    no_sub_title          nvarchar(256) NOT NULL CONSTRAINT df_market_no_sub_title DEFAULT (N''),
    open_time             datetime2(3)  NULL,
    close_time            datetime2(3)  NULL,
    latest_expiration_time datetime2(3) NULL,
    settlement_ts         datetime2(3)  NULL,
    result                varchar(16)   NOT NULL CONSTRAINT df_market_result DEFAULT (''),
    settlement_value_e6   bigint        NULL,
    first_seen_at         datetime2(3)  NOT NULL,
    last_seen_at          datetime2(3)  NOT NULL,
    CONSTRAINT pk_market PRIMARY KEY CLUSTERED (market_id),
    CONSTRAINT uq_market_ticker UNIQUE (ticker)
);
GO
CREATE INDEX ix_market_status ON dbo.market (status) INCLUDE (ticker);
GO

CREATE TABLE dbo.ticker (
    ts                 datetime2(3) NOT NULL,
    market_id          int          NOT NULL,
    price_e6           bigint       NULL,
    yes_bid_e6         bigint       NULL,
    yes_ask_e6         bigint       NULL,
    yes_bid_size_e2    bigint       NULL,
    yes_ask_size_e2    bigint       NULL,
    volume_e2          bigint       NULL,
    open_interest_e2   bigint       NULL,
    last_trade_size_e2 bigint       NULL
) ON ps_day (ts);
GO
CREATE CLUSTERED COLUMNSTORE INDEX cci_ticker ON dbo.ticker ORDER (market_id, ts) ON ps_day (ts);
GO
ALTER TABLE dbo.ticker SET (LOCK_ESCALATION = AUTO);
GO

-- taker_side: 0 unknown, 1 yes, 2 no.
CREATE TABLE dbo.trade (
    ts           datetime2(3) NOT NULL,
    market_id    int          NOT NULL,
    trade_id     varchar(64)  NOT NULL,
    yes_price_e6 bigint       NOT NULL,
    no_price_e6  bigint       NOT NULL,
    count_e2     bigint       NOT NULL,
    taker_side   tinyint      NOT NULL,
    is_block     bit          NOT NULL
) ON ps_day (ts);
GO
CREATE CLUSTERED COLUMNSTORE INDEX cci_trade ON dbo.trade ORDER (market_id, ts) ON ps_day (ts);
GO
ALTER TABLE dbo.trade SET (LOCK_ESCALATION = AUTO);
GO

-- Stretches of time for which data is known to be missing (ingest down, WebSocket reconnect, sequence gap).
CREATE TABLE dbo.ingest_gap (
    gap_id     bigint IDENTITY(1,1) NOT NULL,
    kind       varchar(32)   NOT NULL,
    market_id  int           NULL,
    started_at datetime2(3)  NOT NULL,
    ended_at   datetime2(3)  NULL,
    detail     nvarchar(400) NULL,
    CONSTRAINT pk_ingest_gap PRIMARY KEY CLUSTERED (gap_id)
);
GO
CREATE INDEX ix_ingest_gap_started_at ON dbo.ingest_gap (started_at);
GO

CREATE TABLE dbo.ingest_state (
    name       varchar(64)   NOT NULL,
    value      nvarchar(400) NOT NULL,
    updated_at datetime2(3)  NOT NULL,
    CONSTRAINT pk_ingest_state PRIMARY KEY CLUSTERED (name)
);
GO
