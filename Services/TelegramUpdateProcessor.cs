using AnonymousBot.Data;
using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AnonymousBot.Services;

public sealed class TelegramUpdateProcessor(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ITelegramUpdateLock updateLock,
    IServiceScopeFactory scopeFactory) : ITelegramUpdateProcessor
{
    public async Task<bool> ProcessAsync(
        ITelegramBotClient client,
        Update update,
        CancellationToken cancellationToken)
    {
        await using var lease = await updateLock.AcquireAsync(update.Id, cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.ProcessedTelegramUpdates.AnyAsync(
                item => item.UpdateId == update.Id,
                cancellationToken))
        {
            return false;
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>();
            await handler.HandleAsync(client, update, cancellationToken);
        }

        db.ProcessedTelegramUpdates.Add(new ProcessedTelegramUpdate
        {
            UpdateId = update.Id,
            ProcessedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
