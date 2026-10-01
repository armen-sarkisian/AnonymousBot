using AnonymousBot.Models;

namespace AnonymousBot.Services;

public sealed record AnswerNotification(long OwnerTelegramUserId, string DraftText, string CopyCallbackData);

public static class AnswerNotificationBuilder
{
    public const int TelegramMessageLimit = 4096;
    private const string QuestionPrefix = "Анонимный вопрос:\n\n";
    private const string AnswerSeparator = "\n\nМой ответ:\n\n";

    public static int MaximumAnswerLength(string questionText) =>
        TelegramMessageLimit - QuestionPrefix.Length - questionText.Length - AnswerSeparator.Length;

    public static string CreateDraft(string questionText, string answerText) =>
        $"{QuestionPrefix}{questionText}{AnswerSeparator}{answerText}";

    public static string CreateCopyCallbackData(int questionId)
    {
        if (questionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(questionId));
        }

        return $"answer:copy:{questionId}";
    }

    public static AnswerNotification Create(long ownerTelegramUserId, Question question, Answer answer) =>
        new(
            ownerTelegramUserId,
            CreateDraft(question.Text, answer.Text),
            CreateCopyCallbackData(question.Id));
}
