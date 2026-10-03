using AnonymousBot.Models;

namespace AnonymousBot.Services;

public sealed record AnswerNotification(long OwnerTelegramUserId, string DraftText, string CopyCallbackData);

public static class AnswerNotificationBuilder
{
    public const int TelegramMessageLimit = 4096;

    public static int MaximumAnswerLength(string questionText) =>
        Enum.GetValues<BotLanguage>()
            .Min(language => TelegramMessageLimit -
                             BotMessages.For(language).AnswerDraft(questionText, string.Empty).Length);

    public static string CreateDraft(
        string questionText,
        string answerText,
        BotLanguage language = BotLanguage.Russian) =>
        BotMessages.For(language).AnswerDraft(questionText, answerText);

    public static string CreateCopyCallbackData(int questionId)
    {
        if (questionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(questionId));
        }

        return $"answer:copy:{questionId}";
    }

    public static AnswerNotification Create(
        long ownerTelegramUserId,
        Question question,
        Answer answer,
        BotLanguage language = BotLanguage.Russian) =>
        new(
            ownerTelegramUserId,
            CreateDraft(question.Text, answer.Text, language),
            CreateCopyCallbackData(question.Id));
}
