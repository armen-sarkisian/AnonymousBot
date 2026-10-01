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
    public const string Header = "📨 Новый анонимный вопрос";
    private const string BodyPrefix = Header + "\n\n";

    public static int MaximumQuestionLength => TelegramMessageLimit - BodyPrefix.Length;

    public static QuestionNotification Create(long receiverTelegramUserId, Question question)
    {
        ArgumentNullException.ThrowIfNull(question);

        return new QuestionNotification(
            receiverTelegramUserId,
            BodyPrefix + question.Text,
            [
                new QuestionActionButton("💬 Ответить", CreateCallbackData("answer", question.Id)),
                new QuestionActionButton("🗑 Удалить", CreateCallbackData("delete", question.Id)),
                new QuestionActionButton("🚨 Пожаловаться", CreateCallbackData("report", question.Id))
            ]);
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
