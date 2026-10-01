using AnonymousBot.Models;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace AnonymousBot.Services;

public sealed class TelegramAnswerNotifier(ITelegramBotClient botClient) : IAnswerNotifier
{
    public async Task NotifyAsync(
        long ownerTelegramUserId,
        Question question,
        Answer answer,
        CancellationToken cancellationToken)
    {
        await botClient.SendMessage(
            chatId: ownerTelegramUserId,
            text: "✅ Ответ сохранён.",
            cancellationToken: cancellationToken);

        await SendDraftAsync(ownerTelegramUserId, question, answer, cancellationToken);
    }

    public async Task SendDraftAsync(
        long ownerTelegramUserId,
        Question question,
        Answer answer,
        CancellationToken cancellationToken)
    {
        var notification = AnswerNotificationBuilder.Create(ownerTelegramUserId, question, answer);
        var keyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData("📋 Скопировать", notification.CopyCallbackData));

        await botClient.SendMessage(
            chatId: notification.OwnerTelegramUserId,
            text: notification.DraftText,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }
}
