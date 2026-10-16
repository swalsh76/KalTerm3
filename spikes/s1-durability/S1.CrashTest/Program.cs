using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using Microsoft.Data.SqlClient;

// Spike S-1b: crash consistency. Throwaway code; results go in docs/BUILD-LOG.md.
//   setup                 recreate database S1Test and table dbo.S1Rows
//   selftest              prove the verifier detects missing rows, bad checksums and a zeroed page
//   crash [--cycles N] [--threads T] [--min-sec A] [--max-sec B]
//                         N cycles of: write load, hard-kill sqlservr.exe, restart, verify

const string DataFile = @"C:\KalTermSpikes\s1\sql\S1Test.mdf";
const string LogFile = @"C:\KalTermSpikes\s1\sql\S1Test.ldf";
const string Common = "Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
var masterConn = $"Server=lpc:.;Database=master;{Common}";
var dbConn = $"Server=lpc:.;Database=S1Test;{Common}";

var cmd = args.Length > 0 ? args[0] : "";
int Arg(string name, int dflt) { var i = Array.IndexOf(args, name); return i >= 0 ? int.Parse(args[i + 1]) : dflt; }

switch (cmd)
{
    case "setup": Setup(); return 0;
    case "selftest": return SelfTest() ? 0 : 1;
    case "crash": return Crash(Arg("--cycles", 10), Arg("--threads", 8), Arg("--min-sec", 3), Arg("--max-sec", 8)) ? 0 : 1;
    default: Console.WriteLine("usage: setup | selftest | crash [--cycles N] [--threads T] [--min-sec A] [--max-sec B]"); return 2;
}

// ---------------------------------------------------------------- setup

void Setup()
{
    EnsureOnline(requireDb: false);
    Exec(masterConn, $"""
        IF DB_ID('S1Test') IS NOT NULL BEGIN ALTER DATABASE S1Test SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE S1Test; END;
        CREATE DATABASE S1Test
          ON PRIMARY (NAME = S1Test_data, FILENAME = '{DataFile}', SIZE = 1GB, FILEGROWTH = 256MB)
          LOG ON (NAME = S1Test_log, FILENAME = '{LogFile}', SIZE = 512MB, FILEGROWTH = 256MB);
        ALTER DATABASE S1Test SET RECOVERY SIMPLE;
        ALTER DATABASE S1Test SET PAGE_VERIFY CHECKSUM;
        ALTER DATABASE S1Test SET DELAYED_DURABILITY = DISABLED;
        """);
    Exec(dbConn, "CREATE TABLE dbo.S1Rows (Id bigint NOT NULL PRIMARY KEY, Payload binary(32) NOT NULL);");
    Console.WriteLine("setup: S1Test and dbo.S1Rows recreated");
}

// ---------------------------------------------------------------- crash loop

bool Crash(int cycles, int threads, int minSec, int maxSec)
{
    Setup();
    var rnd = new Random();
    long previousTotal = 0;
    var allOk = true;
    Console.WriteLine($"{"cycle",5} {"acked",9} {"rows",9} {"inflight",8} {"missing",7} {"badsum",6} {"checkdb",8} {"down(s)",8}  result");
    for (var c = 0; c < cycles; c++)
    {
        EnsureOnline();
        using var stop = new CancellationTokenSource();
        var tasks = Enumerable.Range(0, threads)
            .Select(t => Task.Run(() => RunWriter((long)(c * threads + t) * 1_000_000_000L, dbConn, stop.Token)))
            .ToArray();
        Thread.Sleep(TimeSpan.FromSeconds(minSec + rnd.NextDouble() * (maxSec - minSec)));

        var killed = Stopwatch.StartNew();
        foreach (var p in Process.GetProcessesByName("sqlservr")) { p.Kill(); p.WaitForExit(30_000); }
        if (!Task.WaitAll(tasks.Cast<Task>().ToArray(), TimeSpan.FromSeconds(60))) { Console.WriteLine("writers did not stop"); return false; }
        SqlConnection.ClearAllPools();
        EnsureOnline();
        var down = killed.Elapsed.TotalSeconds;

        var report = Verify(tasks.Select(t => t.Result).ToList(), previousTotal, masterConn, dbConn);
        var inflight = report.Total - previousTotal - report.Acked;   // rows this cycle beyond the acknowledged ones
        previousTotal = report.Total;
        var ok = report.Ok;
        allOk &= ok;
        Console.WriteLine($"{c + 1,5} {report.Acked,9} {report.Total,9} {inflight,8} {report.Missing,7} {report.BadChecksums,6} {(report.CheckDb.Count == 0 ? "clean" : "ERRORS"),8} {down,8:F1}  {(ok ? "PASS" : "FAIL")}");
        foreach (var m in report.Problems) Console.WriteLine("      " + m);
    }
    Console.WriteLine(allOk ? "ALL CYCLES PASSED" : "FAILURES");
    return allOk;
}

// ---------------------------------------------------------------- writer

static WriterResult RunWriter(long baseId, string dbConn, CancellationToken stop)
{
    var r = new WriterResult { BaseId = baseId, NextId = baseId, MaxAckedId = baseId - 1 };
    var rnd = new Random(unchecked((int)(baseId / 1000)));
    try
    {
        using var conn = new SqlConnection(dbConn);
        conn.Open();
        while (!stop.IsCancellationRequested)
        {
            var n = rnd.Next(2) == 0 ? 1 : 100;   // single autocommit, or one 100-row transaction
            var first = r.NextId;
            r.NextId += n;                          // attempted; may or may not be durable if we fail
            using var tx = n == 1 ? null : conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandTimeout = 10;
            var sql = new StringBuilder("INSERT dbo.S1Rows (Id, Payload) VALUES ");
            for (var i = 0; i < n; i++)
            {
                sql.Append(i == 0 ? "" : ",").Append($"(@i{i},@p{i})");
                cmd.Parameters.Add(new SqlParameter($"@i{i}", System.Data.SqlDbType.BigInt) { Value = first + i });
                cmd.Parameters.Add(new SqlParameter($"@p{i}", System.Data.SqlDbType.Binary, 32) { Value = Payload(first + i) });
            }
            cmd.CommandText = sql.ToString();
            cmd.ExecuteNonQuery();
            tx?.Commit();
            r.Acked += n;                           // acknowledged by SQL Server
            r.MaxAckedId = first + n - 1;
        }
    }
    catch (Exception ex) { r.Error = ex.GetType().Name; }
    return r;
}

static byte[] Payload(long id)
{
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(b, id);    // matches CONVERT(binary(8), bigint) on the server
    return SHA256.HashData(b);
}

// ---------------------------------------------------------------- verifier

static VerifyReport Verify(List<WriterResult> writers, long previousTotal, string masterConn, string dbConn)
{
    var rep = new VerifyReport();
    try
    {
        rep.CheckDb = CheckDb(masterConn);
        foreach (var w in writers)
        {
            if (w.Acked == 0) continue;
            rep.Acked += w.Acked;
            var present = Scalar(dbConn, $"SELECT COUNT_BIG(*) FROM dbo.S1Rows WHERE Id BETWEEN {w.BaseId} AND {w.MaxAckedId}");
            var expected = w.MaxAckedId - w.BaseId + 1;
            rep.Missing += expected - present;     // acknowledged but not found: durability violated
        }
        rep.BadChecksums = Scalar(dbConn, "SELECT COUNT_BIG(*) FROM dbo.S1Rows WHERE Payload <> HASHBYTES('SHA2_256', CONVERT(binary(8), Id))");
        rep.Total = Scalar(dbConn, "SELECT COUNT_BIG(*) FROM dbo.S1Rows");
    }
    catch (SqlException ex) { rep.Problems.Add($"query failed: {ex.Message}"); rep.QueryFailed = true; }

    foreach (var m in rep.CheckDb) rep.Problems.Add($"CHECKDB: {m}");
    if (rep.Missing > 0) rep.Problems.Add($"{rep.Missing} acknowledged rows are missing");
    if (rep.BadChecksums > 0) rep.Problems.Add($"{rep.BadChecksums} rows fail their checksum");
    if (!rep.QueryFailed && rep.Total < previousTotal) rep.Problems.Add($"row count fell from {previousTotal} to {rep.Total}");
    return rep;
}

static List<string> CheckDb(string masterConn)
{
    var msgs = new List<string>();
    try
    {
        using var c = new SqlConnection(masterConn);
        c.InfoMessage += (_, e) => msgs.Add(e.Message);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 900;
        cmd.CommandText = "DBCC CHECKDB (S1Test) WITH NO_INFOMSGS, ALL_ERRORMSGS, EXTENDED_LOGICAL_CHECKS";
        cmd.ExecuteNonQuery();
    }
    catch (SqlException ex) { msgs.Add(ex.Message); }
    return msgs;
}

// ---------------------------------------------------------------- self test: test the test

bool SelfTest()
{
    Setup();
    var insert = new StringBuilder();
    for (var i = 0; i < 2000; i++) insert.Append($"INSERT dbo.S1Rows VALUES ({i}, HASHBYTES('SHA2_256', CONVERT(binary(8), CAST({i} AS bigint))));");
    Exec(dbConn, insert.ToString());
    var acked = new List<WriterResult> { new() { BaseId = 0, Acked = 2000, MaxAckedId = 1999 } };
    var allOk = true;

    bool Expect(string name, bool condition) { Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {name}"); allOk &= condition; return condition; }

    var clean = Verify(acked, 0, masterConn, dbConn);
    Expect("clean database verifies clean", clean.Ok && clean.Total == 2000);

    Exec(dbConn, "DELETE dbo.S1Rows WHERE Id = 500");
    var missing = Verify(acked, 0, masterConn, dbConn);
    Expect("a deleted acknowledged row is reported missing", missing.Missing == 1 && !missing.Ok);
    Exec(dbConn, "INSERT dbo.S1Rows VALUES (500, HASHBYTES('SHA2_256', CONVERT(binary(8), CAST(500 AS bigint))))");

    Exec(dbConn, "UPDATE dbo.S1Rows SET Payload = 0x00 WHERE Id = 7");
    var badSum = Verify(acked, 0, masterConn, dbConn);
    Expect("a corrupted payload fails its checksum", badSum.BadChecksums == 1 && !badSum.Ok);
    Exec(dbConn, "UPDATE dbo.S1Rows SET Payload = HASHBYTES('SHA2_256', CONVERT(binary(8), CAST(7 AS bigint))) WHERE Id = 7");

    var again = Verify(acked, 0, masterConn, dbConn);
    Expect("repaired database verifies clean again", again.Ok);

    // A wholly zeroed data page: the failure that hid from a scan in the Postgres incident.
    var page = Scalar(masterConn, """
        SELECT MAX(allocated_page_page_id) FROM sys.dm_db_database_page_allocations(DB_ID('S1Test'), OBJECT_ID('S1Test.dbo.S1Rows'), 1, NULL, 'DETAILED')
        WHERE is_allocated = 1 AND page_type_desc = 'DATA_PAGE' AND allocated_page_file_id = 1
        """);
    Exec(masterConn, "ALTER DATABASE S1Test SET OFFLINE WITH ROLLBACK IMMEDIATE");
    using (var fs = new FileStream(DataFile, FileMode.Open, FileAccess.Write, FileShare.None))
    {
        fs.Position = page * 8192;
        fs.Write(new byte[8192]);
        fs.Flush(true);
    }
    Exec(masterConn, "ALTER DATABASE S1Test SET ONLINE");
    SqlConnection.ClearAllPools();
    var zeroed = Verify(acked, 0, masterConn, dbConn);
    Console.WriteLine($"  (zeroed data page {page}: {zeroed.Problems.Count} problem(s) reported)");
    Expect("a wholly zeroed data page is detected", !zeroed.Ok);

    Console.WriteLine(allOk ? "SELFTEST PASSED" : "SELFTEST FAILED");
    return allOk;
}

// ---------------------------------------------------------------- plumbing

static double EnsureOnline(bool requireDb = true)
{
    var sw = Stopwatch.StartNew();
    using var sc = new ServiceController("MSSQLSERVER");
    var conn = $"Server=lpc:.;Database=master;{Common}";
    while (sw.Elapsed < TimeSpan.FromSeconds(180))
    {
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            try { sc.Start(); } catch (InvalidOperationException) { }
        }
        try
        {
            using var c = new SqlConnection(conn);
            c.Open();
            if (!requireDb) return sw.Elapsed.TotalSeconds;
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT state_desc FROM sys.databases WHERE name = 'S1Test'";
            if (cmd.ExecuteScalar() as string == "ONLINE") return sw.Elapsed.TotalSeconds;
        }
        catch (SqlException) { }
        Thread.Sleep(500);
    }
    throw new System.TimeoutException("SQL Server did not come online within 180 s");
}

static void Exec(string conn, string sql)
{
    using var c = new SqlConnection(conn);
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandTimeout = 300;
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static long Scalar(string conn, string sql)
{
    using var c = new SqlConnection(conn);
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandTimeout = 300;
    cmd.CommandText = sql;
    return Convert.ToInt64(cmd.ExecuteScalar());
}

sealed class WriterResult
{
    public long BaseId, NextId, Acked, MaxAckedId;
    public string? Error;
}

sealed class VerifyReport
{
    public long Acked, Total, Missing, BadChecksums;
    public bool QueryFailed;
    public List<string> CheckDb = [];
    public List<string> Problems = [];
    public bool Ok => !QueryFailed && Problems.Count == 0;
}
