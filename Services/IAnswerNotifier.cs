using AnonymousBot.Models;

namespace AnonymousBot.Services;

public interface IAnswerNotifier
{
    Task NotifyAsync(
        long ownerTelegramUserId,
        BotLanguage language,
        Question question,
        Answer answer,
        CancellationToken cancellationToken);

    Task SendDraftAsync(
        long ownerTelegramUserId,
        BotLanguage language,
        Question question,
        Answer answer,
        CancellationToken cancellationToken);
}
