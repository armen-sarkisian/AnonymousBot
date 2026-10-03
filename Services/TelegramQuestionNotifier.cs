using AnonymousBot.Models;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace AnonymousBot.Services;

public sealed class TelegramQuestionNotifier(ITelegramBotClient botClient) : IQuestionNotifier
{
    public async Task NotifyAsync(
        long receiverTelegramUserId,
        BotLanguage language,
        Question question,
        CancellationToken cancellationToken)
    {
        var notification = QuestionNotificationBuilder.Create(receiverTelegramUserId, question, language);
        var buttons = notification.Buttons
            .Select(button => InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackData))
            .ToArray();
        var keyboard = new InlineKeyboardMarkup(buttons);

        await botClient.SendMessage(
            chatId: notification.ReceiverTelegramUserId,
            text: notification.Text,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }
}
