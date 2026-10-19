using KalTerm.Data.Gaps;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Tests;

public class GapStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, 123, DateTimeKind.Utc);

    private GapStore Store() => new(new DataSource(db.Options));

    [Fact]
    public async Task StoresEveryFieldOfEveryGap()
    {
        db.Require();
        var marker = "GS-all-" + Guid.NewGuid().ToString("N");

        await Store().AppendAsync(
            [
                new GapRecord(GapKinds.WsReconnected, T0, T0.AddSeconds(30), marker),
                new GapRecord(GapKinds.WriterDropped, T0.AddHours(1), null, marker, 77),
            ],
            TestContext.Current.CancellationToken);

        await using var connection = await db.OpenAsync();
        await using var command = new SqlCommand("SELECT kind, market_id, started_at, ended_at, detail FROM dbo.ingest_gap WHERE detail = @d ORDER BY gap_id", connection);
        command.Parameters.AddWithValue("@d", marker);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GapKinds.WsReconnected, reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(T0, reader.GetDateTime(2));
        Assert.Equal(T0.AddSeconds(30), reader.GetDateTime(3));
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GapKinds.WriterDropped, reader.GetString(0));
        Assert.Equal(77, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(3));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CutsAnOverlongDetailToTheColumnAndAcceptsNone()
    {
        db.Require();
        var prefix = "GS-long-" + Guid.NewGuid().ToString("N");

        await Store().AppendAsync(
            [
                new GapRecord(GapKinds.WsLost, T0, T0, prefix + new string('x', 1_000)),
                new GapRecord(GapKinds.WsLost, T0, T0, null),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(400, await db.ScalarAsync<int>($"SELECT LEN(detail) FROM dbo.ingest_gap WHERE detail LIKE '{prefix}%'"));
    }

    [Fact]
    public async Task AKindTheColumnCannotHoldIsRefusedAndNothingIsStored()
    {
        db.Require();
        var marker = "GS-atomic-" + Guid.NewGuid().ToString("N");

        // 33 characters: the column is varchar(32), and a parameter would have cut it to 32 without complaint.
        await Assert.ThrowsAsync<ArgumentException>(() => Store().AppendAsync(
            [
                new GapRecord(GapKinds.WsLost, T0, T0, marker),
                new GapRecord(new string('k', 33), T0, T0, marker),
            ],
            TestContext.Current.CancellationToken));

        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT COUNT_BIG(*) FROM dbo.ingest_gap WHERE detail = '{marker}'"));
    }

    [Fact]
    public async Task AnEmptyKindIsRefusedWithoutOpeningAConnection()
    {
        var store = new GapStore(new DataSource(new DatabaseConnectionOptions { Server = "no-such-server-for-this-test" }));

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync([new GapRecord(" ", T0)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEmptyBatchTouchesNothingAndNullIsRefused()
    {
        var store = new GapStore(new DataSource(new DatabaseConnectionOptions { Server = "no-such-server-for-this-test" }));

        await store.AppendAsync([], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AppendAsync(null!, TestContext.Current.CancellationToken));
    }
}
