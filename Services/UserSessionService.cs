using AnonymousBot.Data;
using AnonymousBot.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Services;

public sealed class UserSessionService(AppDbContext db)
{
    public async Task<StartLinkResult> StartFromLinkAsync(
        long senderTelegramUserId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var owner = await db.Users
            .SingleOrDefaultAsync(user => user.Token == token, cancellationToken);

        if (owner is null)
        {
            return new StartLinkResult(StartLinkStatus.InvalidToken);
        }

        if (owner.IsBlocked)
        {
            return new StartLinkResult(StartLinkStatus.BlockedOwner);
        }

        var session = await db.UserSessions
            .SingleOrDefaultAsync(
                candidate => candidate.TelegramUserId == senderTelegramUserId,
                cancellationToken);

        if (session is not null)
        {
            SetWaitingState(session, owner.Id);
            await db.SaveChangesAsync(cancellationToken);
            return new StartLinkResult(StartLinkStatus.Ready);
        }

        var now = DateTime.UtcNow;
        db.UserSessions.Add(new UserSession
        {
            TelegramUserId = senderTelegramUserId,
            State = UserSessionState.WaitingForAnonymousMessage,
            ReceiverUserId = owner.Id,
            CreatedAt = now,
            UpdatedAt = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new StartLinkResult(StartLinkStatus.Ready);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            db.ChangeTracker.Clear();
            session = await db.UserSessions
                .SingleOrDefaultAsync(
                    candidate => candidate.TelegramUserId == senderTelegramUserId,
                    cancellationToken);

            if (session is null)
            {
                throw;
            }

            SetWaitingState(session, owner.Id);
            await db.SaveChangesAsync(cancellationToken);
            return new StartLinkResult(StartLinkStatus.Ready);
        }
    }

    public async Task<int?> ConsumeWaitingMessageAsync(
        long senderTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        var session = await db.UserSessions
            .SingleOrDefaultAsync(
                candidate => candidate.TelegramUserId == senderTelegramUserId &&
                             candidate.State == UserSessionState.WaitingForAnonymousMessage,
                cancellationToken);

        if (session is null)
        {
            return null;
        }

        var receiverUserId = session.ReceiverUserId;
        session.State = UserSessionState.None;
        session.ReceiverUserId = null;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return receiverUserId;
    }

    private static void SetWaitingState(UserSession session, int receiverUserId)
    {
        session.State = UserSessionState.WaitingForAnonymousMessage;
        session.ReceiverUserId = receiverUserId;
        session.UpdatedAt = DateTime.UtcNow;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.GetBaseException() is SqlException { Number: 2601 or 2627 };
}

public enum StartLinkStatus
{
    Ready,
    InvalidToken,
    BlockedOwner
}

public sealed record StartLinkResult(StartLinkStatus Status);
