-- Schema v3 (M4 slice 4.2): trade backfill bookkeeping on the gap log.
-- A closed gap is a period in which the stream may have missed trades. The ingest host recovers them from REST and records the outcome on the gap's own row.
-- backfill_status: pending (not yet tried, or waiting to retry), running, done, failed (out of attempts), skipped (nothing to recover by market-wide REST).
-- Existing rows become pending, so the gaps recorded before this migration are recovered too. Ticker updates and order books are never recovered.

ALTER TABLE dbo.ingest_gap ADD
    backfill_status   varchar(16)   NOT NULL CONSTRAINT df_ingest_gap_backfill_status DEFAULT ('pending'),
    backfill_attempts tinyint       NOT NULL CONSTRAINT df_ingest_gap_backfill_attempts DEFAULT (0),
    backfill_found    int           NULL,   -- trades REST returned for the window
    backfill_added    int           NULL,   -- of those, the trades that were not stored yet and were written
    backfill_at       datetime2(3)  NULL,   -- when the last attempt started (or finished)
    backfill_note     nvarchar(200) NULL;
GO
