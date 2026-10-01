using AnonymousBot.Data;
using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Services;

public sealed class QuestionService(AppDbContext db, IQuestionNotifier notifier)
{
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
        session.UpdatedAt = DateTime.UtcNow;
    }
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
