using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnonymousBot.Services;

public interface ITelegramUpdateHandler
{
    Task HandleAsync(
        ITelegramBotClient client,
        Update update,
        CancellationToken cancellationToken);
}

public interface ITelegramUpdateProcessor
{
    Task<bool> ProcessAsync(
        ITelegramBotClient client,
        Update update,
        CancellationToken cancellationToken);
}

public interface ITelegramUpdateLock
{
    Task<IAsyncDisposable> AcquireAsync(long updateId, CancellationToken cancellationToken);
}
