-- Schema v7 (M5 slice 5.4): the data that belongs to one user: watchlists, settings, alerts and the alerts that fired.
-- Everything hangs off app_user and is only ever read or changed through the caller's own user id.
-- An alert watches one market. threshold is in the unit of the kind: dollars at 10^-6 for price_above, price_below and spread_above; a whole factor for volume_spike (recent 15-minute volume
-- against the average 15-minute volume of the 24 hours before); contracts at 10^-2 for oi_change (open interest moved this much in the last hour, up or down).
-- armed is NULL until the first evaluation, which only records whether the condition already holds (no alert fires for what was true when it was made); then 1 = ready to fire, 0 = fired
-- and waiting for the condition to clear.

CREATE TABLE dbo.watchlist (
    watchlist_id int           IDENTITY(1,1) NOT NULL,
    user_id      int           NOT NULL,
    name         varchar(64)   NOT NULL,
    created_at   datetime2(3)  NOT NULL,
    CONSTRAINT pk_watchlist PRIMARY KEY CLUSTERED (watchlist_id),
    CONSTRAINT fk_watchlist_user FOREIGN KEY (user_id) REFERENCES dbo.app_user (user_id)
);
GO
CREATE UNIQUE INDEX ux_watchlist_user_name ON dbo.watchlist (user_id, name);
GO

CREATE TABLE dbo.watchlist_market (
    watchlist_id int           NOT NULL,
    market_id    int           NOT NULL,
    added_at     datetime2(3)  NOT NULL,
    CONSTRAINT pk_watchlist_market PRIMARY KEY CLUSTERED (watchlist_id, market_id),
    CONSTRAINT fk_watchlist_market_list FOREIGN KEY (watchlist_id) REFERENCES dbo.watchlist (watchlist_id) ON DELETE CASCADE,
    CONSTRAINT fk_watchlist_market_market FOREIGN KEY (market_id) REFERENCES dbo.market (market_id)
);
GO

CREATE TABLE dbo.user_setting (
    user_id    int            NOT NULL,
    name       varchar(32)    NOT NULL,
    value      nvarchar(100)  NOT NULL,
    updated_at datetime2(3)   NOT NULL,
    CONSTRAINT pk_user_setting PRIMARY KEY CLUSTERED (user_id, name),
    CONSTRAINT fk_user_setting_user FOREIGN KEY (user_id) REFERENCES dbo.app_user (user_id)
);
GO

CREATE TABLE dbo.alert (
    alert_id          int           IDENTITY(1,1) NOT NULL,
    user_id           int           NOT NULL,
    market_id         int           NOT NULL,
    kind              varchar(24)   NOT NULL,
    threshold         bigint        NOT NULL,
    cooldown_s        int           NOT NULL,
    paused            bit           NOT NULL CONSTRAINT df_alert_paused DEFAULT (0),
    armed             bit           NULL,
    created_at        datetime2(3)  NOT NULL,
    last_evaluated_at datetime2(3)  NULL,
    last_fired_at     datetime2(3)  NULL,
    CONSTRAINT pk_alert PRIMARY KEY CLUSTERED (alert_id),
    CONSTRAINT fk_alert_user FOREIGN KEY (user_id) REFERENCES dbo.app_user (user_id),
    CONSTRAINT fk_alert_market FOREIGN KEY (market_id) REFERENCES dbo.market (market_id)
);
GO
CREATE INDEX ix_alert_user ON dbo.alert (user_id);
GO
CREATE INDEX ix_alert_active ON dbo.alert (paused, market_id);
GO

CREATE TABLE dbo.alert_fire (
    fire_id    bigint         IDENTITY(1,1) NOT NULL,
    alert_id   int            NOT NULL,
    user_id    int            NOT NULL,
    market_id  int            NOT NULL,
    kind       varchar(24)    NOT NULL,
    threshold  bigint         NOT NULL,
    value      bigint         NOT NULL,   -- what was observed, in the unit of the kind
    fired_at   datetime2(3)   NOT NULL,
    CONSTRAINT pk_alert_fire PRIMARY KEY CLUSTERED (fire_id),
    CONSTRAINT fk_alert_fire_alert FOREIGN KEY (alert_id) REFERENCES dbo.alert (alert_id) ON DELETE CASCADE
);
GO
CREATE INDEX ix_alert_fire_user ON dbo.alert_fire (user_id, fire_id);
GO
