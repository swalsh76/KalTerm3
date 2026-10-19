using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Auth;

/// <summary>The roles a user can have.</summary>
public static class UserRoles
{
    /// <summary>May read market data.</summary>
    public const string Read = "read";

    /// <summary>May also use management endpoints (none exist yet).</summary>
    public const string Admin = "admin";

    /// <summary>Whether a role name is one of ours.</summary>
    /// <param name="role">The name.</param>
    /// <returns>True if it is valid.</returns>
    public static bool IsValid(string? role) => role is Read or Admin;
}

/// <summary>How API tokens look and are stored: <c>kt_</c> and 43 URL-safe characters (256 random bits); only the SHA-256 is kept.</summary>
public static partial class ApiTokens
{
    /// <summary>The prefix of every token, so a leaked one is recognisable.</summary>
    public const string Prefix = "kt_";

    /// <summary>Makes a new random token.</summary>
    /// <returns>The token text, shown to its owner once.</returns>
    public static string Generate() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The hash that is stored in place of the token.</summary>
    /// <param name="token">The token text.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Whether text has the shape of a token, checked before the database is asked anything.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True if it could be a token.</returns>
    public static bool HasShape(string? text) => text is not null && Shape().IsMatch(text);

    [GeneratedRegex("^kt_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}

/// <summary>A user as listed.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Name">The name.</param>
/// <param name="Role">The role.</param>
/// <param name="CreatedAt">When the user was added (UTC).</param>
/// <param name="DisabledAt">When the user was disabled (UTC), or <see langword="null"/>.</param>
public sealed record AppUser(int Id, string Name, string Role, DateTime CreatedAt, DateTime? DisabledAt);

/// <summary>A token as listed (never the token itself).</summary>
/// <param name="Id">The token id.</param>
/// <param name="UserName">The owner.</param>
/// <param name="Label">What it was made for.</param>
/// <param name="CreatedAt">When it was made (UTC).</param>
/// <param name="ExpiresAt">When it stops working (UTC), or <see langword="null"/>.</param>
/// <param name="LastUsedAt">When it last worked (UTC), to within a minute.</param>
/// <param name="RevokedAt">When it was revoked (UTC), or <see langword="null"/>.</param>
public sealed record ApiTokenInfo(int Id, string UserName, string? Label, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt);

/// <summary>Who a valid token belongs to.</summary>
/// <param name="UserId">The user id.</param>
/// <param name="UserName">The user's name.</param>
/// <param name="Role">The user's role.</param>
/// <param name="TokenId">The token id.</param>
public sealed record AuthenticatedUser(int UserId, string UserName, string Role, int TokenId);

/// <summary>Users and their tokens.</summary>
public interface IUserStore
{
    /// <summary>Adds a user.</summary>
    /// <param name="name">A lower-case name of 2 to 32 letters, digits, dots, dashes and underscores, starting with a letter.</param>
    /// <param name="role">The role.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The new user's id.</returns>
    /// <exception cref="InvalidOperationException">The name or role is invalid, or the name is taken.</exception>
    Task<int> AddUserAsync(string name, string role, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>All users, by name.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The users.</returns>
    Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Disables a user: every token of theirs stops working at once (after the API's short cache).</summary>
    /// <param name="name">The user.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether a user was disabled (false when there is none or it was disabled already).</returns>
    Task<bool> DisableUserAsync(string name, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Makes a token for a user.</summary>
    /// <param name="userName">The user.</param>
    /// <param name="label">What it is for.</param>
    /// <param name="expiresAt">When it stops working; <see langword="null"/> for never.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The token's id and its text, which is not stored and cannot be shown again.</returns>
    /// <exception cref="InvalidOperationException">There is no such enabled user.</exception>
    Task<(int Id, string Token)> CreateTokenAsync(string userName, string? label, DateTime? expiresAt, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Tokens, newest first.</summary>
    /// <param name="userName">Only this user's; <see langword="null"/> for everyone's.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The tokens.</returns>
    Task<IReadOnlyList<ApiTokenInfo>> ListTokensAsync(string? userName, CancellationToken cancellationToken = default);

    /// <summary>Revokes a token.</summary>
    /// <param name="tokenId">The token id.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether a token was revoked (false when there is none or it was revoked already).</returns>
    Task<bool> RevokeTokenAsync(int tokenId, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Finds the user a token text belongs to, if it is valid now: known, not revoked, not expired, the user not disabled.</summary>
    /// <param name="token">The token text.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The user, or <see langword="null"/>.</returns>
    Task<AuthenticatedUser?> AuthenticateAsync(string token, DateTime now, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IUserStore"/> over <c>dbo.app_user</c> and <c>dbo.api_token</c>.</summary>
/// <param name="source">Opens connections.</param>
public sealed partial class UserStore(DataSource source) : IUserStore
{
    private const int UniqueViolation = 2601;

    /// <summary>Whether a name is a valid user name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True if valid.</returns>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <inheritdoc/>
    public async Task<int> AddUserAsync(string name, string role, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!IsValidName(name))
        {
            throw new InvalidOperationException("a user name is 2 to 32 lower-case letters, digits, dots, dashes or underscores, starting with a letter");
        }

        if (!UserRoles.IsValid(role))
        {
            throw new InvalidOperationException($"the role must be {UserRoles.Read} or {UserRoles.Admin}");
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "INSERT dbo.app_user (name, role, created_at) OUTPUT inserted.user_id VALUES (@name, @role, @now)",
                new { name, role, now },
                commandTimeout: 30,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == UniqueViolation)
        {
            throw new InvalidOperationException($"there is already a user named {name}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AppUser>(new CommandDefinition(
            "SELECT user_id AS Id, name AS Name, role AS Role, created_at AS CreatedAt, disabled_at AS DisabledAt FROM dbo.app_user ORDER BY name",
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(u => u with { CreatedAt = Utc(u.CreatedAt), DisabledAt = u.DisabledAt is { } d ? Utc(d) : null })];
    }

    /// <inheritdoc/>
    public async Task<bool> DisableUserAsync(string name, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.app_user SET disabled_at = @now WHERE name = @name AND disabled_at IS NULL",
            new { name, now },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<(int Id, string Token)> CreateTokenAsync(string userName, string? label, DateTime? expiresAt, DateTime now, CancellationToken cancellationToken = default)
    {
        var token = ApiTokens.Generate();
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            INSERT dbo.api_token (user_id, token_hash, label, created_at, expires_at)
            OUTPUT inserted.token_id
            SELECT user_id, @hash, @label, @now, @expiresAt FROM dbo.app_user WHERE name = @userName AND disabled_at IS NULL
            """,
            new { userName, hash = ApiTokens.Hash(token), label = label is { Length: > 100 } ? label[..100] : label, now, expiresAt },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return id is { } found ? (found, token) : throw new InvalidOperationException($"there is no enabled user named {userName}");
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ApiTokenInfo>> ListTokensAsync(string? userName, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ApiTokenInfo>(new CommandDefinition(
            """
            SELECT t.token_id AS Id, u.name AS UserName, t.label AS Label, t.created_at AS CreatedAt, t.expires_at AS ExpiresAt, t.last_used_at AS LastUsedAt, t.revoked_at AS RevokedAt
            FROM dbo.api_token t JOIN dbo.app_user u ON u.user_id = t.user_id
            WHERE @userName IS NULL OR u.name = @userName
            ORDER BY t.token_id DESC
            """,
            new { userName },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(t => t with { CreatedAt = Utc(t.CreatedAt), ExpiresAt = Nullable(t.ExpiresAt), LastUsedAt = Nullable(t.LastUsedAt), RevokedAt = Nullable(t.RevokedAt) })];
    }

    /// <inheritdoc/>
    public async Task<bool> RevokeTokenAsync(int tokenId, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.api_token SET revoked_at = @now WHERE token_id = @tokenId AND revoked_at IS NULL",
            new { tokenId, now },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<AuthenticatedUser?> AuthenticateAsync(string token, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!ApiTokens.HasShape(token))
        {
            return null;
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await connection.QuerySingleOrDefaultAsync<AuthenticatedUser>(new CommandDefinition(
            """
            SELECT u.user_id AS UserId, u.name AS UserName, u.role AS Role, t.token_id AS TokenId
            FROM dbo.api_token t JOIN dbo.app_user u ON u.user_id = t.user_id
            WHERE t.token_hash = @hash AND t.revoked_at IS NULL AND u.disabled_at IS NULL AND (t.expires_at IS NULL OR t.expires_at > @now)
            """,
            new { hash = ApiTokens.Hash(token), now },
            commandTimeout: 15,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (found is not null)
        {
            // Recording that the token worked is a convenience for whoever manages tokens; once a minute is plenty and keeps a busy client from writing on every request.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.api_token SET last_used_at = @now WHERE token_id = @id AND (last_used_at IS NULL OR last_used_at < DATEADD(minute, -1, @now))",
                new { id = found.TokenId, now },
                commandTimeout: 15,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return found;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Nullable(DateTime? value) => value is { } moment ? Utc(moment) : null;

    [GeneratedRegex("^[a-z][a-z0-9_.-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
