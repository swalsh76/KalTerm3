-- S-1a step 3: SQL Server single-thread commit latency. Throwaway spike script.
-- Every INSERT below is its own autocommit transaction, i.e. one durable log flush each.
SET NOCOUNT ON;

IF DB_ID('S1Test') IS NOT NULL
BEGIN
    ALTER DATABASE S1Test SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE S1Test;
END;

CREATE DATABASE S1Test
ON PRIMARY (NAME = S1Test_data, FILENAME = 'C:\KalTermSpikes\s1\sql\S1Test.mdf', SIZE = 1GB, FILEGROWTH = 256MB)
LOG ON (NAME = S1Test_log, FILENAME = 'C:\KalTermSpikes\s1\sql\S1Test.ldf', SIZE = 512MB, FILEGROWTH = 256MB);
ALTER DATABASE S1Test SET RECOVERY SIMPLE;
ALTER DATABASE S1Test SET PAGE_VERIFY CHECKSUM;
ALTER DATABASE S1Test SET DELAYED_DURABILITY = DISABLED;
GO
USE S1Test;
CREATE TABLE dbo.Commits (Id bigint IDENTITY(1,1) PRIMARY KEY, Payload binary(64) NOT NULL);
GO
SELECT name, page_verify_option_desc, delayed_durability_desc, recovery_model_desc
FROM sys.databases WHERE name = 'S1Test';

-- Warm up
DECLARE @i int = 0;
WHILE @i < 2000 BEGIN INSERT dbo.Commits (Payload) VALUES (0x01); SET @i += 1; END;
CHECKPOINT;

-- Measure
DECLARE @n int = 20000, @k int = 0;
DECLARE @w0 TABLE (writes bigint, stall bigint);
DECLARE @ws0 TABLE (waits bigint, ms bigint);
INSERT @w0 SELECT num_of_writes, io_stall_write_ms FROM sys.dm_io_virtual_file_stats(DB_ID(), 2);
INSERT @ws0 SELECT waiting_tasks_count, wait_time_ms FROM sys.dm_os_wait_stats WHERE wait_type = 'WRITELOG';

DECLARE @t0 datetime2(7) = SYSUTCDATETIME();
WHILE @k < @n BEGIN INSERT dbo.Commits (Payload) VALUES (0x02); SET @k += 1; END;
DECLARE @ms float = DATEDIFF_BIG(microsecond, @t0, SYSUTCDATETIME()) / 1000.0;

SELECT
    @n AS Commits,
    CAST(@ms AS decimal(12,1)) AS TotalMs,
    CAST(@ms * 1000.0 / @n AS decimal(10,1)) AS MeanUsPerCommit,
    CAST(@n / (@ms / 1000.0) AS decimal(12,0)) AS CommitsPerSec;

SELECT
    f.num_of_writes - w.writes AS LogWrites,
    CAST(1000.0 * (f.io_stall_write_ms - w.stall) / NULLIF(f.num_of_writes - w.writes, 0) AS decimal(10,1)) AS LogWriteStallUsPerWrite
FROM sys.dm_io_virtual_file_stats(DB_ID(), 2) f CROSS JOIN @w0 w;

SELECT
    s.waiting_tasks_count - w.waits AS WriteLogWaits,
    CAST(1000.0 * (s.wait_time_ms - w.ms) / NULLIF(s.waiting_tasks_count - w.waits, 0) AS decimal(10,1)) AS WriteLogWaitUsPerWait
FROM sys.dm_os_wait_stats s CROSS JOIN @ws0 w WHERE s.wait_type = 'WRITELOG';
GO
