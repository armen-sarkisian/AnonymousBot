using AnonymousBot.Models;

namespace AnonymousBot.Services;

public interface IQuestionNotifier
{
    Task NotifyAsync(
        long receiverTelegramUserId,
        BotLanguage language,
        Question question,
        CancellationToken cancellationToken);
}
