using AnonymousBot.Data;
using AnonymousBot.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Services;

public sealed class AnswerService(AppDbContext db, IAnswerNotifier notifier)
{
    public async Task<bool> IsWaitingForAnswerAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default) =>
        await db.UserSessions.AnyAsync(
            session => session.TelegramUserId == telegramUserId &&
                       session.State == UserSessionState.WaitingForAnswer,
            cancellationToken);

    public async Task<AnswerSubmissionResult> SubmitAsync(
        long telegramUserId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        var session = await db.UserSessions.SingleOrDefaultAsync(
            candidate => candidate.TelegramUserId == telegramUserId &&
                         candidate.State == UserSessionState.WaitingForAnswer,
            cancellationToken);
        if (session?.QuestionId is not { } questionId)
        {
            return new AnswerSubmissionResult(AnswerSubmissionStatus.NotWaiting);
        }

        var user = await db.Users.SingleOrDefaultAsync(
            candidate => candidate.TelegramUserId == telegramUserId,
            cancellationToken);
        var question = user is null
            ? null
            : await db.Questions.SingleOrDefaultAsync(
                candidate => candidate.Id == questionId && candidate.ReceiverUserId == user.Id,
                cancellationToken);

        if (question is null || question.Status == QuestionStatus.Deleted)
        {
            ClearSession(session);
            await db.SaveChangesAsync(cancellationToken);
            return new AnswerSubmissionResult(AnswerSubmissionStatus.QuestionUnavailable);
        }

        if (await db.Answers.AnyAsync(answer => answer.QuestionId == question.Id, cancellationToken))
        {
            ClearSession(session);
            await db.SaveChangesAsync(cancellationToken);
            return new AnswerSubmissionResult(AnswerSubmissionStatus.AlreadyAnswered);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new AnswerSubmissionResult(AnswerSubmissionStatus.EmptyText);
        }

        if (text.Length > AnswerNotificationBuilder.MaximumAnswerLength(question.Text))
        {
            return new AnswerSubmissionResult(AnswerSubmissionStatus.TooLong);
        }

        var answer = new Answer
        {
            QuestionId = question.Id,
            Text = text,
            CreatedAt = DateTime.UtcNow
        };
        db.Answers.Add(answer);
        ClearSession(session);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            db.ChangeTracker.Clear();
            session = await db.UserSessions.SingleOrDefaultAsync(
                candidate => candidate.TelegramUserId == telegramUserId &&
                             candidate.State == UserSessionState.WaitingForAnswer &&
                             candidate.QuestionId == questionId,
                cancellationToken);
            if (session is not null)
            {
                ClearSession(session);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new AnswerSubmissionResult(AnswerSubmissionStatus.AlreadyAnswered);
        }

        await notifier.NotifyAsync(user!.TelegramUserId, user.Language, question, answer, cancellationToken);
        return new AnswerSubmissionResult(AnswerSubmissionStatus.Saved);
    }

    public async Task<AnswerDraftResult> GetDraftAsync(
        long telegramUserId,
        int questionId,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(
            candidate => candidate.TelegramUserId == telegramUserId,
            cancellationToken);
        if (user is null)
        {
            return new AnswerDraftResult(AnswerDraftStatus.NotAuthorized, null);
        }

        var question = await db.Questions.SingleOrDefaultAsync(
            candidate => candidate.Id == questionId && candidate.ReceiverUserId == user.Id,
            cancellationToken);
        if (question is null)
        {
            return new AnswerDraftResult(AnswerDraftStatus.NotFoundOrNotOwner, null);
        }

        var answer = await db.Answers.SingleOrDefaultAsync(
            candidate => candidate.QuestionId == question.Id,
            cancellationToken);
        return answer is null
            ? new AnswerDraftResult(AnswerDraftStatus.NotFoundOrNotOwner, null)
            : new AnswerDraftResult(
                AnswerDraftStatus.Ready,
                AnswerNotificationBuilder.Create(user.TelegramUserId, question, answer, user.Language));
    }

    public async Task<QuestionActionStatus> BeginAnswerAsync(
        long telegramUserId,
        int questionId,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(
            candidate => candidate.TelegramUserId == telegramUserId,
            cancellationToken);
        if (user is null)
        {
            return QuestionActionStatus.NotAuthorized;
        }

        var question = await db.Questions.SingleOrDefaultAsync(
            candidate => candidate.Id == questionId && candidate.ReceiverUserId == user.Id,
            cancellationToken);
        if (question is null)
        {
            return QuestionActionStatus.NotFoundOrNotOwner;
        }

        if (question.Status == QuestionStatus.Deleted)
        {
            return QuestionActionStatus.AlreadyDeleted;
        }

        if (await db.Answers.AnyAsync(answer => answer.QuestionId == questionId, cancellationToken))
        {
            return QuestionActionStatus.AlreadyAnswered;
        }

        var session = await db.UserSessions.SingleOrDefaultAsync(
            candidate => candidate.TelegramUserId == telegramUserId,
            cancellationToken);
        if (session is null)
        {
            session = new UserSession
            {
                TelegramUserId = telegramUserId,
                CreatedAt = DateTime.UtcNow
            };
            db.UserSessions.Add(session);
        }

        session.State = UserSessionState.WaitingForAnswer;
        session.ReceiverUserId = null;
        session.QuestionId = questionId;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return QuestionActionStatus.Success;
    }

    private static void ClearSession(UserSession session)
    {
        session.State = UserSessionState.None;
        session.ReceiverUserId = null;
        session.QuestionId = null;
        session.UpdatedAt = DateTime.UtcNow;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.GetBaseException() is SqlException { Number: 2601 or 2627 };
}

public enum AnswerSubmissionStatus
{
    Saved,
    NotWaiting,
    QuestionUnavailable,
    AlreadyAnswered,
    EmptyText,
    TooLong
}

public sealed record AnswerSubmissionResult(AnswerSubmissionStatus Status);

public enum AnswerDraftStatus
{
    Ready,
    NotAuthorized,
    NotFoundOrNotOwner
}

public sealed record AnswerDraftResult(AnswerDraftStatus Status, AnswerNotification? Notification);
