using AnonymousBot.Models;

namespace AnonymousBot.Services;

public sealed record QuestionNotification(
    long ReceiverTelegramUserId,
    string Text,
    IReadOnlyList<QuestionActionButton> Buttons);

public sealed record QuestionActionButton(string Text, string CallbackData);

public static class QuestionNotificationBuilder
{
    public const int TelegramMessageLimit = 4096;

    public static int MaximumQuestionLength => Enum.GetValues<BotLanguage>()
        .Min(language => TelegramMessageLimit -
                        BotMessages.For(language).QuestionNotification(string.Empty).Length);

    public static QuestionNotification Create(
        long receiverTelegramUserId,
        Question question,
        BotLanguage language = BotLanguage.Russian)
    {
        ArgumentNullException.ThrowIfNull(question);

        var messages = BotMessages.For(language);
        return new QuestionNotification(
            receiverTelegramUserId,
            messages.QuestionNotification(question.Text),
            CreateActionButtons(question.Id, language));
    }

    public static IReadOnlyList<QuestionActionButton> CreateActionButtons(
        int questionId,
        BotLanguage language = BotLanguage.Russian)
    {
        var messages = BotMessages.For(language);
        return
        [
            new QuestionActionButton(messages.AnswerButton, CreateCallbackData("answer", questionId)),
            new QuestionActionButton(messages.DeleteButton, CreateCallbackData("delete", questionId)),
            new QuestionActionButton(messages.ReportButton, CreateCallbackData("report", questionId))
        ];
    }

    public static string CreateCallbackData(string action, int questionId)
    {
        if (questionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(questionId));
        }

        if (action is not ("answer" or "delete" or "report"))
        {
            throw new ArgumentException("Unsupported question action.", nameof(action));
        }

        return $"question:{action}:{questionId}";
    }
}
