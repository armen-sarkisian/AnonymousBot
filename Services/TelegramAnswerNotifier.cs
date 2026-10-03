using AnonymousBot.Models;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace AnonymousBot.Services;

public sealed class TelegramAnswerNotifier(ITelegramBotClient botClient) : IAnswerNotifier
{
    public async Task NotifyAsync(
        long ownerTelegramUserId,
        BotLanguage language,
        Question question,
        Answer answer,
        CancellationToken cancellationToken)
    {
        await botClient.SendMessage(
            chatId: ownerTelegramUserId,
            text: BotMessages.For(language).AnswerSaved,
            cancellationToken: cancellationToken);

        await SendDraftAsync(ownerTelegramUserId, language, question, answer, cancellationToken);
    }

    public async Task SendDraftAsync(
        long ownerTelegramUserId,
        BotLanguage language,
        Question question,
        Answer answer,
        CancellationToken cancellationToken)
    {
        var notification = AnswerNotificationBuilder.Create(ownerTelegramUserId, question, answer, language);
        var keyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData(
                BotMessages.For(language).CopyButton,
                notification.CopyCallbackData));

        await botClient.SendMessage(
            chatId: notification.OwnerTelegramUserId,
            text: notification.DraftText,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }
}
