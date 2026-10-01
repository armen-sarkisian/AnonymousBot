using AnonymousBot.Data;
using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Services;

public sealed class QuestionService(AppDbContext db, IQuestionNotifier notifier)
{
    public const int QuestionsPerPage = 5;
    public const string EmptyHistoryText =
        "📨 У тебя пока нет вопросов.\n\nПоделись своей ссылкой, чтобы получить первый анонимный вопрос.";

    public async Task<QuestionHistoryPage> GetMyQuestionsAsync(
        long telegramUserId,
        int requestedPage,
        CancellationToken cancellationToken = default)
    {
        if (requestedPage < 1)
        {
            requestedPage = 1;
        }

        var userId = await FindUserIdAsync(telegramUserId, cancellationToken);
        if (userId is null)
        {
            return new QuestionHistoryPage(false, 0, 1, 0, []);
        }

        var questions = db.Questions
            .AsNoTracking()
            .Where(question =>
                question.ReceiverUserId == userId &&
                question.Status != QuestionStatus.Deleted);

        var totalCount = await questions.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)QuestionsPerPage));
        var page = Math.Min(requestedPage, totalPages);
        var pageItems = await questions
            .OrderBy(question => question.CreatedAt)
            .ThenBy(question => question.Id)
            .Skip((page - 1) * QuestionsPerPage)
            .Take(QuestionsPerPage)
            .Select(question => new QuestionHistoryItem(
                question.Id,
                question.Text,
                question.CreatedAt,
                question.Answer == null ? null : question.Answer.Text))
            .ToListAsync(cancellationToken);
        var items = pageItems
            .Select((item, index) => item with
            {
                Number = (page - 1) * QuestionsPerPage + index + 1
            })
            .ToArray();

        return new QuestionHistoryPage(true, totalCount, page, totalPages, items);
    }

    public async Task<QuestionActionResult> DeleteAsync(
        long telegramUserId,
        int questionId,
        CancellationToken cancellationToken = default)
    {
        var userId = await FindUserIdAsync(telegramUserId, cancellationToken);
        if (userId is null)
        {
            return new QuestionActionResult(QuestionActionStatus.NotAuthorized);
        }

        var question = await db.Questions.SingleOrDefaultAsync(
            candidate => candidate.Id == questionId && candidate.ReceiverUserId == userId,
            cancellationToken);
        if (question is null)
        {
            return new QuestionActionResult(QuestionActionStatus.NotFoundOrNotOwner);
        }

        if (question.Status == QuestionStatus.Deleted)
        {
            return new QuestionActionResult(QuestionActionStatus.AlreadyDeleted);
        }

        question.Status = QuestionStatus.Deleted;
        await db.SaveChangesAsync(cancellationToken);
        return new QuestionActionResult(QuestionActionStatus.Success);
    }

    public Task<bool> IsWaitingForMessageAsync(
        long senderTelegramUserId,
        CancellationToken cancellationToken = default) =>
        db.UserSessions.AnyAsync(
            session => session.TelegramUserId == senderTelegramUserId &&
                       session.State == UserSessionState.WaitingForAnonymousMessage,
            cancellationToken);

    public async Task<QuestionSubmissionResult> SubmitAsync(
        long senderTelegramUserId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        var session = await db.UserSessions.SingleOrDefaultAsync(
            candidate => candidate.TelegramUserId == senderTelegramUserId &&
                         candidate.State == UserSessionState.WaitingForAnonymousMessage,
            cancellationToken);

        if (session is null)
        {
            return new QuestionSubmissionResult(QuestionSubmissionStatus.NotWaiting);
        }

        var receiver = session.ReceiverUserId is { } receiverId
            ? await db.Users.SingleOrDefaultAsync(user => user.Id == receiverId, cancellationToken)
            : null;

        if (receiver is null || receiver.IsBlocked)
        {
            ClearSession(session);
            await db.SaveChangesAsync(cancellationToken);
            return new QuestionSubmissionResult(QuestionSubmissionStatus.ReceiverUnavailable);
        }

        var senderUserId = await db.Users
            .Where(user => user.TelegramUserId == senderTelegramUserId)
            .Select(user => (int?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (senderUserId == receiver.Id)
        {
            ClearSession(session);
            await db.SaveChangesAsync(cancellationToken);
            return new QuestionSubmissionResult(QuestionSubmissionStatus.SelfMessage);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new QuestionSubmissionResult(QuestionSubmissionStatus.EmptyText);
        }

        if (text.Length > QuestionNotificationBuilder.MaximumQuestionLength)
        {
            return new QuestionSubmissionResult(QuestionSubmissionStatus.TooLong);
        }

        var question = new Question
        {
            ReceiverUserId = receiver.Id,
            Text = text,
            CreatedAt = DateTime.UtcNow,
            Status = QuestionStatus.New
        };

        db.Questions.Add(question);
        ClearSession(session);
        await db.SaveChangesAsync(cancellationToken);

        await notifier.NotifyAsync(receiver.TelegramUserId, question, cancellationToken);
        return new QuestionSubmissionResult(QuestionSubmissionStatus.Sent);
    }

    private static void ClearSession(UserSession session)
    {
        session.State = UserSessionState.None;
        session.ReceiverUserId = null;
        session.QuestionId = null;
        session.UpdatedAt = DateTime.UtcNow;
    }

    private Task<int?> FindUserIdAsync(long telegramUserId, CancellationToken cancellationToken) =>
        db.Users
            .Where(user => user.TelegramUserId == telegramUserId)
            .Select(user => (int?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);
}

public enum QuestionSubmissionStatus
{
    Sent,
    NotWaiting,
    ReceiverUnavailable,
    SelfMessage,
    EmptyText,
    TooLong
}

public sealed record QuestionSubmissionResult(QuestionSubmissionStatus Status);

public enum QuestionActionStatus
{
    Success,
    NotFoundOrNotOwner,
    AlreadyDeleted,
    AlreadyAnswered,
    NotAuthorized
}

public sealed record QuestionActionResult(QuestionActionStatus Status);

public sealed record QuestionHistoryItem(int Id, string Text, DateTime CreatedAt, string? AnswerText)
{
    public int Number { get; init; }

    public string ToDisplayText() =>
        $"📨 Вопрос #{Number}\n\n{Text}\n\n💬 Мой ответ:\n{AnswerText ?? "Пока нет ответа"}\n\n📅 Получен: {CreatedAt:dd.MM.yyyy HH:mm}";
}

public sealed record QuestionHistoryPage(
    bool UserExists,
    int TotalCount,
    int Page,
    int TotalPages,
    IReadOnlyList<QuestionHistoryItem> Items)
{
    public bool IsEmpty => UserExists && TotalCount == 0;
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
    public string? EmptyMessage => TotalCount == 0 ? QuestionService.EmptyHistoryText : null;
}
