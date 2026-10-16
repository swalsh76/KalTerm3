namespace KalTerm.Core.Tests;

public class BuildInfoTests
{
    [Fact]
    public void VersionIsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Version));
    }

    [Fact]
    public void VersionStartsWithDigit()
    {
        Assert.True(char.IsDigit(BuildInfo.Version[0]));
    }
}
