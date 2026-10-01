using System.Security.Cryptography;
using AnonymousBot.Data;
using AnonymousBot.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Services;

public sealed class UserService(AppDbContext db)
{
    public async Task<UserStartResult> StartAsync(
        long telegramUserId,
        string? username,
        string? firstName,
        CancellationToken cancellationToken = default)
    {
        var existingUser = await db.Users
            .SingleOrDefaultAsync(user => user.TelegramUserId == telegramUserId, cancellationToken);

        if (existingUser is not null)
        {
            await UpdateProfileAsync(existingUser, username, firstName, cancellationToken);
            return new UserStartResult(IsNewUser: false, IsBlocked: existingUser.IsBlocked);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var user = new User
            {
                TelegramUserId = telegramUserId,
                Username = username,
                FirstName = firstName,
                Token = CreateToken(),
                CreatedAt = DateTime.UtcNow,
                IsBlocked = false
            };

            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new UserStartResult(IsNewUser: true, IsBlocked: false);
            }
            catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
            {
                db.ChangeTracker.Clear();

                existingUser = await db.Users
                    .SingleOrDefaultAsync(
                        candidate => candidate.TelegramUserId == telegramUserId,
                        cancellationToken);

                if (existingUser is not null)
                {
                    await UpdateProfileAsync(existingUser, username, firstName, cancellationToken);
                    return new UserStartResult(IsNewUser: false, IsBlocked: existingUser.IsBlocked);
                }

                if (attempt == 2)
                {
                    throw;
                }
            }
        }

        throw new InvalidOperationException("Could not create a unique user token after multiple attempts.");
    }

    public Task<bool> IsBlockedAsync(long telegramUserId, CancellationToken cancellationToken = default) =>
        db.Users
            .Where(user => user.TelegramUserId == telegramUserId)
            .Select(user => user.IsBlocked)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<UserLinkResult> GetLinkAsync(
        long telegramUserId,
        string? botUsername,
        CancellationToken cancellationToken = default)
    {
        var token = await db.Users
            .Where(user => user.TelegramUserId == telegramUserId)
            .Select(user => user.Token)
            .SingleOrDefaultAsync(cancellationToken);

        if (token is null)
        {
            return new UserLinkResult(UserExists: false, Link: null);
        }

        if (string.IsNullOrWhiteSpace(botUsername))
        {
            return new UserLinkResult(UserExists: true, Link: null);
        }

        return new UserLinkResult(
            UserExists: true,
            Link: TelegramLinkBuilder.Create(botUsername, token));
    }

    private async Task UpdateProfileAsync(
        User user,
        string? username,
        string? firstName,
        CancellationToken cancellationToken)
    {
        if (user.Username == username && user.FirstName == firstName)
        {
            return;
        }

        user.Username = username;
        user.FirstName = firstName;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.GetBaseException() is SqlException { Number: 2601 or 2627 };
}

public sealed record UserStartResult(bool IsNewUser, bool IsBlocked)
{
    public bool HasAccess => !IsBlocked;
}

public sealed record UserLinkResult(bool UserExists, string? Link);

public static class TelegramLinkBuilder
{
    public static string Create(string botUsername, string token)
    {
        var normalizedUsername = botUsername.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalizedUsername))
        {
            throw new ArgumentException("Bot username must be configured.", nameof(botUsername));
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("User token cannot be empty.", nameof(token));
        }

        return $"https://t.me/{normalizedUsername}?start={Uri.EscapeDataString(token)}";
    }
}
