using AnonymousBot.Data;
using AnonymousBot.Models;
using AnonymousBot.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnonymousBot.Tests;

public sealed class QuestionServiceTests
{
    [Fact]
    public async Task SubmitAsync_SavesQuestionNotifiesOwnerAndClearsSession()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, telegramUserId: 456);
        var sender = await AddUserAsync(db, telegramUserId: 123, username: "sender-name", firstName: "Sender");
        await AddWaitingSessionAsync(db, sender.TelegramUserId, owner.Id);
        var notifier = new RecordingQuestionNotifier(db);
        var service = new QuestionService(db, notifier);

        var result = await service.SubmitAsync(sender.TelegramUserId, "Вопрос анонимно");

        var question = await db.Questions.SingleAsync();
        var session = await db.UserSessions.SingleAsync();
        Assert.Equal(QuestionSubmissionStatus.Sent, result.Status);
        Assert.Equal(owner.Id, question.ReceiverUserId);
        Assert.Equal("Вопрос анонимно", question.Text);
        Assert.Equal(QuestionStatus.New, question.Status);
        Assert.Equal(DateTimeKind.Utc, question.CreatedAt.Kind);
        Assert.Equal(sender.Id, await db.Users
            .Where(user => user.TelegramUserId == sender.TelegramUserId)
            .Select(user => user.Id)
            .SingleAsync());
        Assert.Equal(UserSessionState.None, session.State);
        Assert.Null(session.ReceiverUserId);
        Assert.Equal(456, notifier.ReceiverTelegramUserId);
        Assert.Equal(question.Id, notifier.QuestionId);
        Assert.True(notifier.QuestionWasPersistedBeforeNotification);
        Assert.True(notifier.SessionWasClearedBeforeNotification);
        Assert.DoesNotContain("sender-name", notifier.NotificationText);
        Assert.DoesNotContain("Sender", notifier.NotificationText);
        Assert.DoesNotContain(sender.TelegramUserId.ToString(), notifier.NotificationText);
    }

    [Fact]
    public async Task SubmitAsync_DoesNotSendQuestionToSelf()
    {
        await using var db = CreateDbContext();
        var user = await AddUserAsync(db, telegramUserId: 123);
        await AddWaitingSessionAsync(db, user.TelegramUserId, user.Id);
        var notifier = new RecordingQuestionNotifier(db);
        var service = new QuestionService(db, notifier);

        var result = await service.SubmitAsync(user.TelegramUserId, "self message");

        Assert.Equal(QuestionSubmissionStatus.SelfMessage, result.Status);
        Assert.Empty(await db.Questions.ToListAsync());
        Assert.Empty(notifier.Notifications);
        Assert.Equal(UserSessionState.None, (await db.UserSessions.SingleAsync()).State);
    }

    [Fact]
    public async Task SubmitAsync_DoesNotSendQuestionToBlockedReceiver()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, telegramUserId: 456, isBlocked: true);
        await AddWaitingSessionAsync(db, senderTelegramUserId: 123, owner.Id);
        var notifier = new RecordingQuestionNotifier(db);
        var service = new QuestionService(db, notifier);

        var result = await service.SubmitAsync(123, "question");

        Assert.Equal(QuestionSubmissionStatus.ReceiverUnavailable, result.Status);
        Assert.Empty(await db.Questions.ToListAsync());
        Assert.Empty(notifier.Notifications);
        Assert.Equal(UserSessionState.None, (await db.UserSessions.SingleAsync()).State);
    }

    [Fact]
    public async Task SubmitAsync_EmptyTextIsNotSavedAndSessionRemainsWaiting()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, telegramUserId: 456);
        await AddWaitingSessionAsync(db, senderTelegramUserId: 123, owner.Id);
        var notifier = new RecordingQuestionNotifier(db);
        var service = new QuestionService(db, notifier);

        var result = await service.SubmitAsync(123, " \t ");

        Assert.Equal(QuestionSubmissionStatus.EmptyText, result.Status);
        Assert.Empty(await db.Questions.ToListAsync());
        Assert.Empty(notifier.Notifications);
        Assert.Equal(
            UserSessionState.WaitingForAnonymousMessage,
            (await db.UserSessions.SingleAsync()).State);
    }

    [Fact]
    public void Question_DoesNotContainSenderUserId()
    {
        Assert.Null(typeof(Question).GetProperty("SenderUserId"));
    }

    [Fact]
    public void QuestionNotificationBuilder_UsesOnlyQuestionIdAndActionInCallbackData()
    {
        var notification = QuestionNotificationBuilder.Create(
            456,
            new Question { Id = 42, Text = "anonymous text" });

        Assert.Collection(
            notification.Buttons,
            answer => Assert.Equal("question:answer:42", answer.CallbackData),
            delete => Assert.Equal("question:delete:42", delete.CallbackData),
            report => Assert.Equal("question:report:42", report.CallbackData));
        Assert.Contains(QuestionNotificationBuilder.Header, notification.Text);
        Assert.Contains("anonymous text", notification.Text);
    }

    [Fact]
    public async Task SubmitAsync_RejectsTextThatWouldExceedTelegramMessageLimit()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, telegramUserId: 456);
        await AddWaitingSessionAsync(db, senderTelegramUserId: 123, owner.Id);
        var notifier = new RecordingQuestionNotifier(db);
        var service = new QuestionService(db, notifier);

        var result = await service.SubmitAsync(
            123,
            new string('x', QuestionNotificationBuilder.MaximumQuestionLength + 1));

        Assert.Equal(QuestionSubmissionStatus.TooLong, result.Status);
        Assert.Empty(await db.Questions.ToListAsync());
        Assert.Empty(notifier.Notifications);
        Assert.Equal(
            UserSessionState.WaitingForAnonymousMessage,
            (await db.UserSessions.SingleAsync()).State);
    }

    private static async Task<User> AddUserAsync(
        AppDbContext db,
        long telegramUserId,
        bool isBlocked = false,
        string? username = null,
        string? firstName = null)
    {
        var user = new User
        {
            TelegramUserId = telegramUserId,
            Token = $"token-{telegramUserId}",
            IsBlocked = isBlocked,
            Username = username,
            FirstName = firstName
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task AddWaitingSessionAsync(
        AppDbContext db,
        long senderTelegramUserId,
        int receiverUserId)
    {
        db.UserSessions.Add(new UserSession
        {
            TelegramUserId = senderTelegramUserId,
            State = UserSessionState.WaitingForAnonymousMessage,
            ReceiverUserId = receiverUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private sealed class RecordingQuestionNotifier(AppDbContext db) : IQuestionNotifier
    {
        public List<QuestionNotification> Notifications { get; } = [];
        public long ReceiverTelegramUserId { get; private set; }
        public int QuestionId { get; private set; }
        public string NotificationText { get; private set; } = string.Empty;
        public bool QuestionWasPersistedBeforeNotification { get; private set; }
        public bool SessionWasClearedBeforeNotification { get; private set; }

        public Task NotifyAsync(
            long receiverTelegramUserId,
            Question question,
            CancellationToken cancellationToken)
        {
            ReceiverTelegramUserId = receiverTelegramUserId;
            QuestionId = question.Id;
            var notification = QuestionNotificationBuilder.Create(receiverTelegramUserId, question);
            Notifications.Add(notification);
            NotificationText = notification.Text;
            QuestionWasPersistedBeforeNotification = db.Questions.Any(item => item.Id == question.Id);
            SessionWasClearedBeforeNotification =
                db.UserSessions.All(session => session.State == UserSessionState.None);
            return Task.CompletedTask;
        }
    }
}
