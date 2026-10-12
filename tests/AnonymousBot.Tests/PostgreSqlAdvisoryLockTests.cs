using AnonymousBot.Services;
using Xunit;

namespace AnonymousBot.Tests;

public sealed class PostgreSqlAdvisoryLockTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1234567890123)]
    [InlineData(long.MaxValue)]
    public void ForTelegramUpdate_UsesPositiveLockKey(long updateId)
    {
        Assert.Equal(updateId, PostgreSqlAdvisoryLockKeys.ForTelegramUpdate(updateId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1234567890123)]
    [InlineData(long.MaxValue)]
    public void ForAnonymousQuestionSender_UsesNegativeLockKey(long telegramUserId)
    {
        Assert.Equal(-telegramUserId, PostgreSqlAdvisoryLockKeys.ForAnonymousQuestionSender(telegramUserId));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1234567890123, 1234567890123)]
    public void TelegramUpdateAndSenderKeys_AreInDisjointNamespaces(long id, long senderId)
    {
        var updateKey = PostgreSqlAdvisoryLockKeys.ForTelegramUpdate(id);
        var senderKey = PostgreSqlAdvisoryLockKeys.ForAnonymousQuestionSender(senderId);

        Assert.True(updateKey > 0);
        Assert.True(senderKey < 0);
        Assert.NotEqual(updateKey, senderKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ForTelegramUpdate_RejectsNonPositiveIds(long updateId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PostgreSqlAdvisoryLockKeys.ForTelegramUpdate(updateId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ForAnonymousQuestionSender_RejectsNonPositiveIds(long telegramUserId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PostgreSqlAdvisoryLockKeys.ForAnonymousQuestionSender(telegramUserId));
    }
}
