using KalTerm.Data.Auth;

namespace KalTerm.Data.Tests;

public class ApiTokensTests
{
    [Fact]
    public void ATokenHasThePrefixAnd256BitsAndNeverRepeats()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => ApiTokens.Generate()).ToList();

        Assert.All(tokens, t => Assert.True(ApiTokens.HasShape(t), t));
        Assert.All(tokens, t => Assert.Equal(46, t.Length));
        Assert.Equal(200, tokens.Distinct().Count());
        Assert.StartsWith("kt_", tokens[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheStoredHashIsTheSha256OfTheTokenAndDiffersForDifferentTokens()
    {
        var token = "kt_" + new string('A', 43);

        var hash = ApiTokens.Hash(token);

        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, ApiTokens.Hash(token));
        Assert.NotEqual(hash, ApiTokens.Hash("kt_" + new string('B', 43)));
        Assert.Throws<ArgumentNullException>(() => ApiTokens.Hash(null!));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("kt_short", false)]
    [InlineData("xx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("kt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("kt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("kt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA AAAAAAA", false)]
    public void OnlyTextWithTheShapeOfATokenIsEverLookedUp(string? text, bool shaped)
    {
        Assert.Equal(shaped, ApiTokens.HasShape(text));
    }

    [Theory]
    [InlineData("steven", true)]
    [InlineData("a1", true)]
    [InlineData("first.last-2_x", true)]
    [InlineData("S", false)]
    [InlineData("Steven", false)]
    [InlineData("1abc", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    [InlineData("this-name-is-way-too-long-for-the-column", false)]
    public void UserNamesAreLowerCaseAndShort(string name, bool valid)
    {
        Assert.Equal(valid, UserStore.IsValidName(name));
        Assert.False(UserStore.IsValidName(null));
        Assert.True(UserRoles.IsValid("read") && UserRoles.IsValid("admin"));
        Assert.False(UserRoles.IsValid("root") || UserRoles.IsValid(null));
    }
}

public class UserStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private UserStore Store() => new(new DataSource(db.Options));

    private static string Name(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task AUserIsAddedListedAndANameCanBeUsedOnlyOnce()
    {
        db.Require();
        var name = Name("ann");

        var id = await Store().AddUserAsync(name, UserRoles.Read, Now, TestContext.Current.CancellationToken);
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => Store().AddUserAsync(name, UserRoles.Admin, Now, TestContext.Current.CancellationToken));
        var users = await Store().ListUsersAsync(TestContext.Current.CancellationToken);

        Assert.True(id > 0);
        Assert.Contains("already a user", duplicate.Message, StringComparison.Ordinal);
        var listed = users.Single(u => u.Name == name);
        Assert.Equal((id, UserRoles.Read, Now, DateTimeKind.Utc, (DateTime?)null), (listed.Id, listed.Role, listed.CreatedAt, listed.CreatedAt.Kind, listed.DisabledAt));
    }

    [Fact]
    public async Task BadNamesAndRolesAreRefusedBeforeTheDatabaseIsTouched()
    {
        var store = new UserStore(new DataSource(new DatabaseConnectionOptions { Server = "no-such-server-for-this-test" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddUserAsync("Bad Name", UserRoles.Read, Now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddUserAsync("fine", "root", Now, TestContext.Current.CancellationToken));
        Assert.Null(await store.AuthenticateAsync("not a token", Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATokenIsShownOnceStoredOnlyAsAHashAndAuthenticatesItsUser()
    {
        db.Require();
        var name = Name("bob");
        await Store().AddUserAsync(name, UserRoles.Admin, Now, TestContext.Current.CancellationToken);

        var (id, token) = await Store().CreateTokenAsync(name, "laptop", null, Now, TestContext.Current.CancellationToken);
        var user = await Store().AuthenticateAsync(token, Now.AddMinutes(5), TestContext.Current.CancellationToken);
        var wrong = await Store().AuthenticateAsync(ApiTokens.Generate(), Now, TestContext.Current.CancellationToken);

        Assert.True(ApiTokens.HasShape(token));
        Assert.Equal((name, UserRoles.Admin, id), (user!.UserName, user.Role, user.TokenId));
        Assert.Null(wrong);
        var listed = (await Store().ListTokensAsync(name, TestContext.Current.CancellationToken)).Single();
        Assert.Equal(("laptop", Now.AddMinutes(5)), (listed.Label, listed.LastUsedAt));
    }

    [Fact]
    public async Task LastUsedIsWrittenAtMostOncePerMinute()
    {
        db.Require();
        var name = Name("cat");
        await Store().AddUserAsync(name, UserRoles.Read, Now, TestContext.Current.CancellationToken);
        var (_, token) = await Store().CreateTokenAsync(name, null, null, Now, TestContext.Current.CancellationToken);

        await Store().AuthenticateAsync(token, Now, TestContext.Current.CancellationToken);
        await Store().AuthenticateAsync(token, Now.AddSeconds(30), TestContext.Current.CancellationToken);
        var afterTwo = (await Store().ListTokensAsync(name, TestContext.Current.CancellationToken)).Single().LastUsedAt;
        await Store().AuthenticateAsync(token, Now.AddSeconds(90), TestContext.Current.CancellationToken);
        var afterThree = (await Store().ListTokensAsync(name, TestContext.Current.CancellationToken)).Single().LastUsedAt;

        Assert.Equal(Now, afterTwo);
        Assert.Equal(Now.AddSeconds(90), afterThree);
    }

    [Fact]
    public async Task ARevokedTokenAnExpiredTokenAndADisabledUsersTokenAllStopWorking()
    {
        db.Require();
        var name = Name("dan");
        await Store().AddUserAsync(name, UserRoles.Read, Now, TestContext.Current.CancellationToken);
        var (revokedId, revoked) = await Store().CreateTokenAsync(name, "old", null, Now, TestContext.Current.CancellationToken);
        var (_, expiring) = await Store().CreateTokenAsync(name, "short", Now.AddHours(1), Now, TestContext.Current.CancellationToken);
        var (_, kept) = await Store().CreateTokenAsync(name, "kept", null, Now, TestContext.Current.CancellationToken);

        var beforeExpiry = await Store().AuthenticateAsync(expiring, Now.AddMinutes(59), TestContext.Current.CancellationToken);
        var afterExpiry = await Store().AuthenticateAsync(expiring, Now.AddMinutes(61), TestContext.Current.CancellationToken);
        var didRevoke = await Store().RevokeTokenAsync(revokedId, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        var revokedAgain = await Store().RevokeTokenAsync(revokedId, Now.AddMinutes(2), TestContext.Current.CancellationToken);
        var afterRevoke = await Store().AuthenticateAsync(revoked, Now.AddMinutes(3), TestContext.Current.CancellationToken);
        var stillGood = await Store().AuthenticateAsync(kept, Now.AddMinutes(3), TestContext.Current.CancellationToken);
        var disabled = await Store().DisableUserAsync(name, Now.AddMinutes(4), TestContext.Current.CancellationToken);
        var disabledAgain = await Store().DisableUserAsync(name, Now.AddMinutes(5), TestContext.Current.CancellationToken);
        var afterDisable = await Store().AuthenticateAsync(kept, Now.AddMinutes(6), TestContext.Current.CancellationToken);

        Assert.NotNull(beforeExpiry);
        Assert.Null(afterExpiry);
        Assert.True(didRevoke);
        Assert.False(revokedAgain);
        Assert.Null(afterRevoke);
        Assert.NotNull(stillGood);
        Assert.True(disabled);
        Assert.False(disabledAgain);
        Assert.Null(afterDisable);
        Assert.NotNull((await Store().ListUsersAsync(TestContext.Current.CancellationToken)).Single(u => u.Name == name).DisabledAt);
        Assert.False(await Store().DisableUserAsync("nobody-" + Guid.NewGuid().ToString("N")[..6], Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TokensCannotBeMadeForUnknownOrDisabledUsersAndTheListShowsWhatHappenedToEach()
    {
        db.Require();
        var name = Name("eve");
        await Store().AddUserAsync(name, UserRoles.Read, Now, TestContext.Current.CancellationToken);
        var (firstId, _) = await Store().CreateTokenAsync(name, new string('x', 300), Now.AddDays(30), Now, TestContext.Current.CancellationToken);
        var (secondId, _) = await Store().CreateTokenAsync(name, null, null, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        await Store().RevokeTokenAsync(firstId, Now.AddMinutes(2), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().CreateTokenAsync("nobody-" + Guid.NewGuid().ToString("N")[..6], null, null, Now, TestContext.Current.CancellationToken));
        await Store().DisableUserAsync(name, Now.AddMinutes(3), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().CreateTokenAsync(name, null, null, Now, TestContext.Current.CancellationToken));
        var mine = await Store().ListTokensAsync(name, TestContext.Current.CancellationToken);
        var everyone = await Store().ListTokensAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal([secondId, firstId], mine.Select(t => t.Id));
        Assert.Equal(100, mine[1].Label!.Length);
        Assert.Equal((Now.AddDays(30), Now.AddMinutes(2), DateTimeKind.Utc), (mine[1].ExpiresAt, mine[1].RevokedAt, mine[1].CreatedAt.Kind));
        Assert.True(everyone.Count >= 2);
    }
}
