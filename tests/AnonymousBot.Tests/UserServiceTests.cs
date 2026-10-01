using AnonymousBot.Data;
using AnonymousBot.Models;
using AnonymousBot.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnonymousBot.Tests;

public sealed class UserServiceTests
{
    [Fact]
    public async Task StartAsync_CreatesNewUserWithUniqueTokenAndUtcTimestamp()
    {
        await using var db = CreateDbContext();
        var service = new UserService(db);
        var before = DateTime.UtcNow;

        var result = await service.StartAsync(telegramUserId: 123, "test_user", "Test");

        var user = await db.Users.SingleAsync();
        Assert.True(result.IsNewUser);
        Assert.True(result.HasAccess);
        Assert.Equal(123, user.TelegramUserId);
        Assert.Equal("test_user", user.Username);
        Assert.Equal("Test", user.FirstName);
        Assert.False(string.IsNullOrWhiteSpace(user.Token));
        Assert.Equal(22, user.Token.Length);
        Assert.False(user.IsBlocked);
        Assert.Equal(DateTimeKind.Utc, user.CreatedAt.Kind);
        Assert.InRange(user.CreatedAt, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task StartAsync_RepeatedStartDoesNotCreateAnotherUser()
    {
        await using var db = CreateDbContext();
        var service = new UserService(db);

        await service.StartAsync(telegramUserId: 123, "test_user", "Test");
        var result = await service.StartAsync(telegramUserId: 123, "test_user", "Test");

        Assert.False(result.IsNewUser);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task StartAsync_UpdatesChangedUsernameAndFirstName()
    {
        await using var db = CreateDbContext();
        var service = new UserService(db);
        await service.StartAsync(telegramUserId: 123, "old_user", "Old name");

        await service.StartAsync(telegramUserId: 123, "new_user", "New name");

        var user = await db.Users.SingleAsync();
        Assert.Equal("new_user", user.Username);
        Assert.Equal("New name", user.FirstName);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task StartAsync_BlockedUserDoesNotHaveAccess()
    {
        await using var db = CreateDbContext();
        db.Users.Add(new User
        {
            TelegramUserId = 123,
            Token = "existing-token",
            IsBlocked = true
        });
        await db.SaveChangesAsync();
        var service = new UserService(db);

        var result = await service.StartAsync(telegramUserId: 123, "test_user", "Test");

        Assert.True(result.IsBlocked);
        Assert.False(result.HasAccess);
        Assert.True(await service.IsBlockedAsync(123));
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public void TelegramLinkBuilder_CreatesDeepLink()
    {
        var link = TelegramLinkBuilder.Create("@example_bot", "user-token");

        Assert.Equal("https://t.me/example_bot?start=user-token", link);
    }

    [Fact]
    public async Task GetLinkAsync_RepeatedCallsKeepTheSameToken()
    {
        await using var db = CreateDbContext();
        var user = new User
        {
            TelegramUserId = 123,
            Token = "permanent-token"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new UserService(db);

        var firstLink = await service.GetLinkAsync(123, "example_bot");
        var secondLink = await service.GetLinkAsync(123, "example_bot");

        Assert.Equal("https://t.me/example_bot?start=permanent-token", firstLink.Link);
        Assert.Equal(firstLink, secondLink);
        Assert.True(firstLink.UserExists);
        Assert.Equal("permanent-token", (await db.Users.SingleAsync()).Token);
    }

    [Fact]
    public async Task GetLinkAsync_ReturnsNullForMissingUserWithoutCreatingOne()
    {
        await using var db = CreateDbContext();
        var service = new UserService(db);

        var link = await service.GetLinkAsync(telegramUserId: 123, "example_bot");

        Assert.False(link.UserExists);
        Assert.Null(link.Link);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task StartFromLinkAsync_ValidTokenCreatesSessionForOwner()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, telegramUserId: 456, "owner-token");
        var service = new UserSessionService(db);

        var result = await service.StartFromLinkAsync(senderTelegramUserId: 123, "owner-token");

        var session = await db.UserSessions.SingleAsync();
        Assert.Equal(StartLinkStatus.Ready, result.Status);
        Assert.Equal(123, session.TelegramUserId);
        Assert.Equal(UserSessionState.WaitingForAnonymousMessage, session.State);
        Assert.Equal(owner.Id, session.ReceiverUserId);
        Assert.Equal(DateTimeKind.Utc, session.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, session.UpdatedAt.Kind);
    }

    [Fact]
    public async Task StartFromLinkAsync_InvalidTokenDoesNotCreateSession()
    {
        await using var db = CreateDbContext();
        var service = new UserSessionService(db);

        var result = await service.StartFromLinkAsync(senderTelegramUserId: 123, "missing-token");

        Assert.Equal(StartLinkStatus.InvalidToken, result.Status);
        Assert.Empty(await db.UserSessions.ToListAsync());
    }

    [Fact]
    public async Task StartFromLinkAsync_BlockedOwnerDoesNotCreateSession()
    {
        await using var db = CreateDbContext();
        await AddUserAsync(db, telegramUserId: 456, "blocked-token", isBlocked: true);
        var service = new UserSessionService(db);

        var result = await service.StartFromLinkAsync(senderTelegramUserId: 123, "blocked-token");

        Assert.Equal(StartLinkStatus.BlockedOwner, result.Status);
        Assert.Empty(await db.UserSessions.ToListAsync());
    }

    [Fact]
    public async Task StartFromLinkAsync_SecondTokenReplacesSenderSession()
    {
        await using var db = CreateDbContext();
        var firstOwner = await AddUserAsync(db, telegramUserId: 456, "first-token");
        var secondOwner = await AddUserAsync(db, telegramUserId: 789, "second-token");
        var service = new UserSessionService(db);
        await service.StartFromLinkAsync(senderTelegramUserId: 123, "first-token");

        await service.StartFromLinkAsync(senderTelegramUserId: 123, "second-token");

        var session = await db.UserSessions.SingleAsync();
        Assert.Equal(1, await db.UserSessions.CountAsync());
        Assert.NotEqual(firstOwner.Id, session.ReceiverUserId);
        Assert.Equal(secondOwner.Id, session.ReceiverUserId);
        Assert.Equal(UserSessionState.WaitingForAnonymousMessage, session.State);
    }

    [Fact]
    public async Task ConsumeWaitingMessageAsync_ClearsSessionWithoutSavingMessageText()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, telegramUserId: 456, "owner-token");
        var service = new UserSessionService(db);
        await service.StartFromLinkAsync(senderTelegramUserId: 123, "owner-token");

        var receiverUserId = await service.ConsumeWaitingMessageAsync(123);

        var session = await db.UserSessions.SingleAsync();
        Assert.Equal(owner.Id, receiverUserId);
        Assert.Equal(UserSessionState.None, session.State);
        Assert.Null(session.ReceiverUserId);
        Assert.Empty(await db.Messages.ToListAsync());
    }

    private static async Task<User> AddUserAsync(
        AppDbContext db,
        long telegramUserId,
        string token,
        bool isBlocked = false)
    {
        var user = new User
        {
            TelegramUserId = telegramUserId,
            Token = token,
            IsBlocked = isBlocked
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
