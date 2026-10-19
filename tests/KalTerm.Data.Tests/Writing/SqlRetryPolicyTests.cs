using KalTerm.Data.Writing;

namespace KalTerm.Data.Tests.Writing;

public class SqlRetryPolicyTests
{
    [Theory]
    [InlineData(1205, 13)]
    [InlineData(4891, 16)]
    [InlineData(-2, 11)]
    [InlineData(-1, 20)]
    [InlineData(2, 20)]
    [InlineData(53, 20)]
    [InlineData(9001, 21)]
    [InlineData(4060, 11)]
    [InlineData(922, 14)]
    [InlineData(945, 14)]
    [InlineData(18456, 14)]
    public void RetriesDeadlocksLockConflictsTimeoutsAndLostConnections(int number, int severity)
    {
        Assert.True(SqlRetryPolicy.IsRetryable(number, severity));
    }

    [Theory]
    [InlineData(208, 16)]
    [InlineData(515, 16)]
    [InlineData(2628, 16)]
    [InlineData(8152, 16)]
    [InlineData(2627, 14)]
    public void DoesNotRetryRefusedValuesOrAMissingTable(int number, int severity)
    {
        Assert.False(SqlRetryPolicy.IsRetryable(number, severity));
    }

    [Fact]
    public void RetriesTimeoutsAndIoErrorsButNotOtherExceptions()
    {
        Assert.True(SqlRetryPolicy.IsRetryable(new TimeoutException()));
        Assert.True(SqlRetryPolicy.IsRetryable(new IOException()));
        Assert.False(SqlRetryPolicy.IsRetryable(new InvalidOperationException()));
        Assert.False(SqlRetryPolicy.IsRetryable(new ArgumentException("x")));
    }

    [Fact]
    public void NullIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => SqlRetryPolicy.IsRetryable(null!));
    }
}
