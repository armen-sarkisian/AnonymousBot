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

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
