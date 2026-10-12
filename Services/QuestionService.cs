using System.Data;
using System.Diagnostics;
using AnonymousBot.Data;
using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AnonymousBot.Services;

public sealed class QuestionService(AppDbContext db, IQuestionNotifier notifier)
{
    public const int QuestionsPerPage = 5;
    public const int AnonymousQuestionLimit = 5;
    public static readonly TimeSpan AnonymousQuestionWindow = TimeSpan.FromMinutes(10);

    public async Task<QuestionHistoryPage> GetMyQuestionsAsync(
        long telegramUserId,
        int requestedPage,
        CancellationToken cancellationToken = default,
        BotLanguage language = BotLanguage.Russian)
    {
        if (requestedPage < 1)
        {
            requestedPage = 1;
        }

        var userId = await FindUserIdAsync(telegramUserId, cancellationToken);
        if (userId is null)
        {
            return new QuestionHistoryPage(false, 0, 1, 0, [], language);
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
                question.Answer == null ? null : question.Answer.Text,
                language))
            .ToListAsync(cancellationToken);
        var items = pageItems
            .Select((item, index) => item with
            {
                Number = (page - 1) * QuestionsPerPage + index + 1
            })
            .ToArray();

        return new QuestionHistoryPage(true, totalCount, page, totalPages, items, language);
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

    public async Task<QuestionActionResult> ReportAsync(
        long telegramUserId,
        int questionId,
        CancellationToken cancellationToken = default)
    {
        var reporter = await db.Users.SingleOrDefaultAsync(
            user => user.TelegramUserId == telegramUserId,
            cancellationToken);
        if (reporter is null)
        {
            return new QuestionActionResult(QuestionActionStatus.NotAuthorized);
        }

        var question = await db.Questions.SingleOrDefaultAsync(
            candidate => candidate.Id == questionId && candidate.ReceiverUserId == reporter.Id,
            cancellationToken);
        if (question is null)
        {
            return new QuestionActionResult(QuestionActionStatus.NotFoundOrNotOwner);
        }

        if (question.Status == QuestionStatus.Deleted)
        {
            return new QuestionActionResult(QuestionActionStatus.AlreadyDeleted);
        }

        if (await db.QuestionReports.AnyAsync(
                report => report.QuestionId == questionId && report.ReporterUserId == reporter.Id,
                cancellationToken))
        {
            return new QuestionActionResult(QuestionActionStatus.AlreadyReported);
        }

        db.QuestionReports.Add(new QuestionReport
        {
            QuestionId = questionId,
            ReporterUserId = reporter.Id,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            return new QuestionActionResult(QuestionActionStatus.AlreadyReported);
        }

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
        IDbContextTransaction? transaction = null;
        Question? questionToNotify = null;
        User? receiverToNotify = null;
        QuestionSubmissionResult result;

        try
        {
            var providerName = db.Database.ProviderName;
            if (string.Equals(providerName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            {
                transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
                var acquired = await AcquirePostgreSqlSenderLockAsync(
                    senderTelegramUserId,
                    transaction,
                    cancellationToken);
                if (!acquired)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new QuestionSubmissionResult(QuestionSubmissionStatus.RateLimited);
                }
            }
            else if (!string.Equals(
                         providerName,
                         "Microsoft.EntityFrameworkCore.InMemory",
                         StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"Anonymous-question rate limiting is not configured for EF Core provider '{providerName}'.");
            }

            (result, questionToNotify, receiverToNotify) = await SubmitUnderLockAsync(
                senderTelegramUserId,
                text,
                cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        if (questionToNotify is not null && receiverToNotify is not null)
        {
            await notifier.NotifyAsync(
                receiverToNotify.TelegramUserId,
                receiverToNotify.Language,
                questionToNotify,
                cancellationToken);
        }

        return result;
    }

    private async Task<(QuestionSubmissionResult Result, Question? Question, User? Receiver)> SubmitUnderLockAsync(
        long senderTelegramUserId,
        string? text,
        CancellationToken cancellationToken)
    {
        var session = await db.UserSessions.SingleOrDefaultAsync(
            candidate => candidate.TelegramUserId == senderTelegramUserId &&
                         candidate.State == UserSessionState.WaitingForAnonymousMessage,
            cancellationToken);

        if (session is null)
        {
            return (new QuestionSubmissionResult(QuestionSubmissionStatus.NotWaiting), null, null);
        }

        var receiver = session.ReceiverUserId is { } receiverId
            ? await db.Users.SingleOrDefaultAsync(user => user.Id == receiverId, cancellationToken)
            : null;

        if (receiver is null || receiver.IsBlocked)
        {
            ClearSession(session);
            await db.SaveChangesAsync(cancellationToken);
            return (new QuestionSubmissionResult(QuestionSubmissionStatus.ReceiverUnavailable), null, null);
        }

        var senderUserId = await db.Users
            .Where(user => user.TelegramUserId == senderTelegramUserId)
            .Select(user => (int?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (senderUserId == receiver.Id)
        {
            ClearSession(session);
            await db.SaveChangesAsync(cancellationToken);
            return (new QuestionSubmissionResult(QuestionSubmissionStatus.SelfMessage), null, null);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return (new QuestionSubmissionResult(QuestionSubmissionStatus.EmptyText), null, null);
        }

        if (text.Length > QuestionNotificationBuilder.MaximumQuestionLength)
        {
            return (new QuestionSubmissionResult(QuestionSubmissionStatus.TooLong), null, null);
        }

        var now = DateTime.UtcNow;
        var cutoff = now - AnonymousQuestionWindow;
        var expiredAttempts = await db.AnonymousMessageAttempts
            .Where(attempt =>
                attempt.TelegramUserId == senderTelegramUserId &&
                attempt.CreatedAt <= cutoff)
            .ToListAsync(cancellationToken);
        db.AnonymousMessageAttempts.RemoveRange(expiredAttempts);

        var recentAttempts = await db.AnonymousMessageAttempts.CountAsync(
            attempt =>
                attempt.TelegramUserId == senderTelegramUserId &&
                attempt.CreatedAt > cutoff,
            cancellationToken);
        if (recentAttempts >= AnonymousQuestionLimit)
        {
            await db.SaveChangesAsync(cancellationToken);
            return (new QuestionSubmissionResult(QuestionSubmissionStatus.RateLimited), null, null);
        }

        var question = new Question
        {
            ReceiverUserId = receiver.Id,
            Text = text,
            CreatedAt = now,
            Status = QuestionStatus.New
        };

        db.AnonymousMessageAttempts.Add(new AnonymousMessageAttempt
        {
            TelegramUserId = senderTelegramUserId,
            CreatedAt = now
        });
        db.Questions.Add(question);
        ClearSession(session);
        await db.SaveChangesAsync(cancellationToken);

        return (new QuestionSubmissionResult(QuestionSubmissionStatus.Sent), question, receiver);
    }

    private static async Task<bool> AcquirePostgreSqlSenderLockAsync(
        long senderTelegramUserId,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var lockKey = PostgreSqlAdvisoryLockKeys.ForAnonymousQuestionSender(senderTelegramUserId);
        var connection = transaction.GetDbTransaction().Connection
            ?? throw new InvalidOperationException("The rate-limit transaction has no database connection.");
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(5);
        var retryInterval = TimeSpan.FromMilliseconds(100);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SELECT pg_try_advisory_xact_lock(@lockKey);";
            command.CommandTimeout = 5;
            PostgreSqlTelegramUpdateLock.AddLockKeyParameter(command, lockKey);
            if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
            {
                return true;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(
                remaining < retryInterval ? remaining : retryInterval,
                cancellationToken);
        }
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

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        PostgreSqlDatabaseErrors.IsUniqueViolation(exception);
}

public enum QuestionSubmissionStatus
{
    Sent,
    NotWaiting,
    ReceiverUnavailable,
    SelfMessage,
    EmptyText,
    TooLong,
    RateLimited
}

public sealed record QuestionSubmissionResult(QuestionSubmissionStatus Status);

public enum QuestionActionStatus
{
    Success,
    NotFoundOrNotOwner,
    AlreadyDeleted,
    AlreadyAnswered,
    NotAuthorized,
    AlreadyReported
}

public sealed record QuestionActionResult(QuestionActionStatus Status);

public sealed record QuestionHistoryItem(
    int Id,
    string Text,
    DateTime CreatedAt,
    string? AnswerText,
    BotLanguage Language = BotLanguage.Russian)
{
    public int Number { get; init; }

    public string ToDisplayText() =>
        BotMessages.For(Language).QuestionHistoryItem(Number, Text, AnswerText, CreatedAt);
}

public sealed record QuestionHistoryPage(
    bool UserExists,
    int TotalCount,
    int Page,
    int TotalPages,
    IReadOnlyList<QuestionHistoryItem> Items,
    BotLanguage Language = BotLanguage.Russian)
{
    public bool IsEmpty => UserExists && TotalCount == 0;
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
    public string? EmptyMessage => TotalCount == 0 ? BotMessages.For(Language).EmptyHistory : null;
}
