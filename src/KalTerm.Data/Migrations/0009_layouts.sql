-- Schema v9 (M7 slice 7.7): saved layouts. A layout is how one user likes the terminal laid out (window, filters, columns, chart defaults); the server stores it and never reads it.
-- body is a JSON object the terminal writes and reads; the CHECK keeps anything else out. A user has at most a few layouts (the store enforces the limit), each up to a size the API enforces.

CREATE TABLE dbo.user_layout (
    user_id    int            NOT NULL,
    name       varchar(64)    NOT NULL,
    body       nvarchar(max)  NOT NULL,
    updated_at datetime2(3)   NOT NULL,
    CONSTRAINT pk_user_layout PRIMARY KEY CLUSTERED (user_id, name),
    CONSTRAINT fk_user_layout_user FOREIGN KEY (user_id) REFERENCES dbo.app_user (user_id),
    CONSTRAINT ck_user_layout_json CHECK (ISJSON(body) = 1)
);
GO
