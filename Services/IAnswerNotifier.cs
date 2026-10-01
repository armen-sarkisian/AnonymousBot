using AnonymousBot.Models;

namespace AnonymousBot.Services;

public interface IAnswerNotifier
{
    Task NotifyAsync(
        long ownerTelegramUserId,
        Question question,
        Answer answer,
        CancellationToken cancellationToken);

    Task SendDraftAsync(
        long ownerTelegramUserId,
        Question question,
        Answer answer,
        CancellationToken cancellationToken);
}
