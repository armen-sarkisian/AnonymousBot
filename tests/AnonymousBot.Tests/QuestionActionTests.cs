using AnonymousBot.Data;
using AnonymousBot.Models;
using AnonymousBot.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnonymousBot.Tests;

public sealed class QuestionActionTests
{
    [Fact]
    public async Task DeleteAsync_OwnerSoftDeletesQuestion()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var question = await AddQuestionAsync(db, owner.Id);
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var result = await service.DeleteAsync(owner.TelegramUserId, question.Id);

        Assert.Equal(QuestionActionStatus.Success, result.Status);
        Assert.Equal(QuestionStatus.Deleted, (await db.Questions.SingleAsync()).Status);
        Assert.Equal(1, await db.Questions.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_NonOwnerCannotDeleteQuestion()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var other = await AddUserAsync(db, 123);
        var question = await AddQuestionAsync(db, owner.Id);
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var result = await service.DeleteAsync(other.TelegramUserId, question.Id);

        Assert.Equal(QuestionActionStatus.NotFoundOrNotOwner, result.Status);
        Assert.Equal(QuestionStatus.New, (await db.Questions.SingleAsync()).Status);
    }

    [Fact]
    public async Task DeleteAsync_DeletedQuestionCannotBeDeletedTwice()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var question = await AddQuestionAsync(db, owner.Id);
        var service = new QuestionService(db, new RecordingQuestionNotifier());
        await service.DeleteAsync(owner.TelegramUserId, question.Id);

        var result = await service.DeleteAsync(owner.TelegramUserId, question.Id);

        Assert.Equal(QuestionActionStatus.AlreadyDeleted, result.Status);
        Assert.Equal(1, await db.Questions.CountAsync());
    }

    [Fact]
    public async Task BeginAnswerAsync_OwnerCreatesWaitingSession()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var question = await AddQuestionAsync(db, owner.Id);
        var service = new AnswerService(db, new RecordingAnswerNotifier());

        var result = await service.BeginAnswerAsync(owner.TelegramUserId, question.Id);

        var session = await db.UserSessions.SingleAsync();
        Assert.Equal(QuestionActionStatus.Success, result);
        Assert.Equal(UserSessionState.WaitingForAnswer, session.State);
        Assert.Equal(question.Id, session.QuestionId);
        Assert.Null(session.ReceiverUserId);
    }

    [Fact]
    public async Task BeginAnswerAsync_NonOwnerCannotStartAnswer()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var other = await AddUserAsync(db, 123);
        var question = await AddQuestionAsync(db, owner.Id);
        var service = new AnswerService(db, new RecordingAnswerNotifier());

        var result = await service.BeginAnswerAsync(other.TelegramUserId, question.Id);

        Assert.Equal(QuestionActionStatus.NotFoundOrNotOwner, result);
        Assert.Empty(await db.UserSessions.ToListAsync());
    }

    [Fact]
    public async Task BeginAnswerAsync_DeletedQuestionCannotBeAnswered()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var question = await AddQuestionAsync(db, owner.Id, QuestionStatus.Deleted);
        var service = new AnswerService(db, new RecordingAnswerNotifier());

        var result = await service.BeginAnswerAsync(owner.TelegramUserId, question.Id);

        Assert.Equal(QuestionActionStatus.AlreadyDeleted, result);
        Assert.Empty(await db.UserSessions.ToListAsync());
    }

    [Fact]
    public async Task SubmitAsync_SavesAnswerAndClearsSession()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var question = await AddQuestionAsync(db, owner.Id);
        await AddAnswerSessionAsync(db, owner.TelegramUserId, question.Id);
        var notifier = new RecordingAnswerNotifier(db);
        var service = new AnswerService(db, notifier);

        var result = await service.SubmitAsync(owner.TelegramUserId, "Мой ответ");

        var answer = await db.Answers.SingleAsync();
        var session = await db.UserSessions.SingleAsync();
        Assert.Equal(AnswerSubmissionStatus.Saved, result.Status);
        Assert.Equal(question.Id, answer.QuestionId);
        Assert.Equal("Мой ответ", answer.Text);
        Assert.Equal(DateTimeKind.Utc, answer.CreatedAt.Kind);
        Assert.Equal(UserSessionState.None, session.State);
        Assert.Null(session.QuestionId);
        Assert.Null(session.ReceiverUserId);
        Assert.Equal(2, notifier.MessagesSent);
        Assert.Equal(owner.TelegramUserId, notifier.RecipientTelegramUserId);
        Assert.True(notifier.AnswerWasPersistedBeforeNotification);
        Assert.True(notifier.SessionWasClearedBeforeNotification);
        Assert.Equal(
            $"Анонимный вопрос:\n\n{question.Text}\n\nМой ответ:\n\nМой ответ",
            notifier.DraftText);
    }

    [Fact]
    public async Task SubmitAsync_DoesNotCreateSecondAnswer()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var question = await AddQuestionAsync(db, owner.Id);
        db.Answers.Add(new Answer
        {
            QuestionId = question.Id,
            Text = "existing",
            CreatedAt = DateTime.UtcNow
        });
        await AddAnswerSessionAsync(db, owner.TelegramUserId, question.Id);
        var notifier = new RecordingAnswerNotifier(db);
        var service = new AnswerService(db, notifier);

        var result = await service.SubmitAsync(owner.TelegramUserId, "second");

        Assert.Equal(AnswerSubmissionStatus.AlreadyAnswered, result.Status);
        Assert.Equal(1, await db.Answers.CountAsync());
        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task SubmitAsync_CannotAnswerDeletedQuestion()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var question = await AddQuestionAsync(db, owner.Id, QuestionStatus.Deleted);
        await AddAnswerSessionAsync(db, owner.TelegramUserId, question.Id);
        var notifier = new RecordingAnswerNotifier(db);
        var service = new AnswerService(db, notifier);

        var result = await service.SubmitAsync(owner.TelegramUserId, "answer");

        Assert.Equal(AnswerSubmissionStatus.QuestionUnavailable, result.Status);
        Assert.Empty(await db.Answers.ToListAsync());
        Assert.Empty(notifier.Notifications);
        Assert.Equal(UserSessionState.None, (await db.UserSessions.SingleAsync()).State);
    }

    [Fact]
    public async Task SubmitAsync_EmptyAnswerIsNotSaved()
    {
        await using var db = CreateDbContext();
        var owner = await AddUserAsync(db, 456);
        var question = await AddQuestionAsync(db, owner.Id);
        await AddAnswerSessionAsync(db, owner.TelegramUserId, question.Id);
        var service = new AnswerService(db, new RecordingAnswerNotifier(db));

        var result = await service.SubmitAsync(owner.TelegramUserId, " \t ");

        Assert.Equal(AnswerSubmissionStatus.EmptyText, result.Status);
        Assert.Empty(await db.Answers.ToListAsync());
        Assert.Equal(UserSessionState.WaitingForAnswer, (await db.UserSessions.SingleAsync()).State);
    }

    [Fact]
    public void Answer_DoesNotContainSenderIdentity()
    {
        Assert.Null(typeof(Answer).GetProperty("SenderUserId"));
        Assert.Null(typeof(Answer).GetProperty("SenderTelegramUserId"));
    }

    [Fact]
    public void AnswerCopyCallback_ContainsOnlyActionAndQuestionId()
    {
        Assert.Equal(
            "answer:copy:17",
            AnswerNotificationBuilder.CreateCopyCallbackData(17));
    }

    private static async Task<User> AddUserAsync(AppDbContext db, long telegramUserId)
    {
        var user = new User
        {
            TelegramUserId = telegramUserId,
            Token = $"token-{telegramUserId}"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Question> AddQuestionAsync(
        AppDbContext db,
        int receiverUserId,
        QuestionStatus status = QuestionStatus.New)
    {
        var question = new Question
        {
            ReceiverUserId = receiverUserId,
            Text = "Анонимный вопрос",
            CreatedAt = DateTime.UtcNow,
            Status = status
        };
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        return question;
    }

    private static async Task AddAnswerSessionAsync(AppDbContext db, long telegramUserId, int questionId)
    {
        db.UserSessions.Add(new UserSession
        {
            TelegramUserId = telegramUserId,
            State = UserSessionState.WaitingForAnswer,
            QuestionId = questionId,
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

    private sealed class RecordingQuestionNotifier : IQuestionNotifier
    {
        public Task NotifyAsync(
            long receiverTelegramUserId,
            BotLanguage language,
            Question question,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingAnswerNotifier(AppDbContext? db = null) : IAnswerNotifier
    {
        public List<AnswerNotification> Notifications { get; } = [];
        public int MessagesSent { get; private set; }
        public long RecipientTelegramUserId { get; private set; }
        public string DraftText { get; private set; } = string.Empty;
        public bool AnswerWasPersistedBeforeNotification { get; private set; }
        public bool SessionWasClearedBeforeNotification { get; private set; }

        public Task NotifyAsync(
            long ownerTelegramUserId,
            BotLanguage language,
            Question question,
            Answer answer,
            CancellationToken cancellationToken)
        {
            var notification = AnswerNotificationBuilder.Create(ownerTelegramUserId, question, answer, language);
            Notifications.Add(notification);
            RecipientTelegramUserId = ownerTelegramUserId;
            DraftText = notification.DraftText;
            MessagesSent = 2;
            AnswerWasPersistedBeforeNotification = db?.Answers.Any(item => item.Id == answer.Id) ?? false;
            SessionWasClearedBeforeNotification =
                db?.UserSessions.All(session => session.State == UserSessionState.None) ?? false;
            return Task.CompletedTask;
        }

        public Task SendDraftAsync(
            long ownerTelegramUserId,
            BotLanguage language,
            Question question,
            Answer answer,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
