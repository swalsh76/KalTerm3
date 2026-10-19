-- Schema v4 (M4 slice 4.5): named users and their API tokens.
-- A token is 256 random bits shown once when it is made; only its SHA-256 is stored, so a copy of the database does not hold a usable token.
-- roles: read (market data) and admin (reserved for management endpoints). Per-user watchlists, alerts and layouts (M5/M7) will hang off app_user.

CREATE TABLE dbo.app_user (
    user_id     int           IDENTITY(1,1) NOT NULL,
    name        varchar(32)   NOT NULL,
    role        varchar(16)   NOT NULL,
    created_at  datetime2(3)  NOT NULL,
    disabled_at datetime2(3)  NULL,
    CONSTRAINT pk_app_user PRIMARY KEY CLUSTERED (user_id)
);
GO
CREATE UNIQUE INDEX ux_app_user_name ON dbo.app_user (name);
GO

CREATE TABLE dbo.api_token (
    token_id     int           IDENTITY(1,1) NOT NULL,
    user_id      int           NOT NULL,
    token_hash   binary(32)    NOT NULL,
    label        nvarchar(100) NULL,
    created_at   datetime2(3)  NOT NULL,
    expires_at   datetime2(3)  NULL,
    last_used_at datetime2(3)  NULL,
    revoked_at   datetime2(3)  NULL,
    CONSTRAINT pk_api_token PRIMARY KEY CLUSTERED (token_id),
    CONSTRAINT fk_api_token_user FOREIGN KEY (user_id) REFERENCES dbo.app_user (user_id)
);
GO
CREATE UNIQUE INDEX ux_api_token_hash ON dbo.api_token (token_hash);
GO
