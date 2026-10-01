using AnonymousBot.Data;
using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Services;

public sealed class QuestionService(AppDbContext db, IQuestionNotifier notifier)
{
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
