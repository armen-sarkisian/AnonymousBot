namespace AnonymousBot.Services;

public static class PostgreSqlAdvisoryLockKeys
{
    public static long ForTelegramUpdate(long updateId)
    {
        if (updateId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updateId), "Telegram update IDs must be positive.");
        }

        return updateId;
    }

    public static long ForAnonymousQuestionSender(long telegramUserId)
    {
        if (telegramUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(telegramUserId),
                "Telegram user IDs must be positive.");
        }

        return -telegramUserId;
    }
}
