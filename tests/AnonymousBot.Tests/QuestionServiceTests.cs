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
        Assert.Equal(sender.TelegramUserId,
            (await db.AnonymousMessageAttempts.SingleAsync()).TelegramUserId);
    }

    [Fact]
    public async Task SubmitAsync_AllowsTheFifthQuestionWithinTenMinutes()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var service = new QuestionService(db, new RecordingQuestionNotifier(db));

        for (var attempt = 0; attempt < QuestionService.AnonymousQuestionLimit; attempt++)
        {
            await AddWaitingSessionAsync(db, 123, owner.Id);
            var result = await service.SubmitAsync(123, $"question {attempt}");
            Assert.Equal(QuestionSubmissionStatus.Sent, result.Status);
        }

        Assert.Equal(5, await db.Questions.CountAsync());
        Assert.Equal(5, await db.AnonymousMessageAttempts.CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_RejectsTheSixthQuestionWithoutSavingIt()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var service = new QuestionService(db, new RecordingQuestionNotifier(db));

        for (var attempt = 0; attempt < QuestionService.AnonymousQuestionLimit; attempt++)
        {
            await AddWaitingSessionAsync(db, 123, owner.Id);
            Assert.Equal(
                QuestionSubmissionStatus.Sent,
                (await service.SubmitAsync(123, $"question {attempt}")).Status);
        }

        await AddWaitingSessionAsync(db, 123, owner.Id);
        var result = await service.SubmitAsync(123, "sixth question");

        Assert.Equal(QuestionSubmissionStatus.RateLimited, result.Status);
        Assert.Equal(
            "Слишком много сообщений.\n\nПопробуй отправить вопрос немного позже.",
            BotMessages.For(BotLanguage.Russian).RateLimitExceeded);
        Assert.Equal(5, await db.Questions.CountAsync());
        Assert.Equal(5, await db.AnonymousMessageAttempts.CountAsync());
        Assert.Equal(
            UserSessionState.WaitingForAnonymousMessage,
            (await db.UserSessions.SingleAsync()).State);
    }

    [Fact]
    public async Task SubmitAsync_AllowsAnotherQuestionAfterTenMinuteWindowExpires()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        db.AnonymousMessageAttempts.AddRange(Enumerable.Range(0, QuestionService.AnonymousQuestionLimit)
            .Select(_ => new AnonymousMessageAttempt
            {
                TelegramUserId = 123,
                CreatedAt = DateTime.UtcNow - QuestionService.AnonymousQuestionWindow - TimeSpan.FromSeconds(1)
            }));
        await db.SaveChangesAsync();
        await AddWaitingSessionAsync(db, 123, owner.Id);
        var service = new QuestionService(db, new RecordingQuestionNotifier(db));

        var result = await service.SubmitAsync(123, "after cooldown");

        Assert.Equal(QuestionSubmissionStatus.Sent, result.Status);
        Assert.Equal(1, await db.AnonymousMessageAttempts.CountAsync());
        Assert.Single(await db.Questions.ToListAsync());
    }

    [Fact]
    public async Task SubmitAsync_RateLimitIsIndependentForEachSender()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        db.AnonymousMessageAttempts.AddRange(Enumerable.Range(0, QuestionService.AnonymousQuestionLimit)
            .Select(_ => new AnonymousMessageAttempt
            {
                TelegramUserId = 123,
                CreatedAt = DateTime.UtcNow
            }));
        await db.SaveChangesAsync();
        await AddWaitingSessionAsync(db, 789, owner.Id);
        var service = new QuestionService(db, new RecordingQuestionNotifier(db));

        var result = await service.SubmitAsync(789, "another sender");

        Assert.Equal(QuestionSubmissionStatus.Sent, result.Status);
        Assert.Equal(QuestionService.AnonymousQuestionLimit + 1, await db.AnonymousMessageAttempts.CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_ParallelMessagesCannotCreateMoreThanOneQuestionForTheSameSession()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        await AddWaitingSessionAsync(db, 123, owner.Id);
        var service = new QuestionService(db, new RecordingQuestionNotifier(db));

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(index => service.SubmitAsync(123, $"parallel question {index}")));

        Assert.Single(await db.Questions.ToListAsync());
        Assert.Single(await db.AnonymousMessageAttempts.ToListAsync());
        Assert.Contains(results, result => result.Status == QuestionSubmissionStatus.Sent);
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
        Assert.Empty(await db.AnonymousMessageAttempts.ToListAsync());
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
        Assert.Empty(await db.AnonymousMessageAttempts.ToListAsync());
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
        Assert.Empty(await db.AnonymousMessageAttempts.ToListAsync());
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
        Assert.Contains(BotMessages.For(BotLanguage.Russian).QuestionNotification(string.Empty).Trim(),
            notification.Text);
        Assert.Contains("anonymous text", notification.Text);
    }

    [Theory]
    [InlineData(BotLanguage.Russian)]
    [InlineData(BotLanguage.Ukrainian)]
    [InlineData(BotLanguage.English)]
    public void CreateActionButtons_UsesQuestionOnlyCallbacks(BotLanguage language)
    {
        var buttons = QuestionNotificationBuilder.CreateActionButtons(42, language);

        Assert.Collection(
            buttons,
            answer => Assert.Equal("question:answer:42", answer.CallbackData),
            delete => Assert.Equal("question:delete:42", delete.CallbackData),
            report => Assert.Equal("question:report:42", report.CallbackData));
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
        var session = await db.UserSessions.SingleOrDefaultAsync(
            candidate => candidate.TelegramUserId == senderTelegramUserId);
        if (session is null)
        {
            session = new UserSession
            {
                TelegramUserId = senderTelegramUserId,
                CreatedAt = DateTime.UtcNow
            };
            db.UserSessions.Add(session);
        }

        session.State = UserSessionState.WaitingForAnonymousMessage;
        session.ReceiverUserId = receiverUserId;
        session.QuestionId = null;
        session.UpdatedAt = DateTime.UtcNow;
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
            BotLanguage language,
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
