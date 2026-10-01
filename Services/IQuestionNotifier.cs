using AnonymousBot.Models;

namespace AnonymousBot.Services;

public interface IQuestionNotifier
{
    Task NotifyAsync(long receiverTelegramUserId, Question question, CancellationToken cancellationToken);
}
