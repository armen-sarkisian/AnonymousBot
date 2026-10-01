using AnonymousBot.Data;
using AnonymousBot.Models;
using AnonymousBot.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnonymousBot.Tests;

public sealed class QuestionHistoryTests
{
    [Fact]
    public async Task GetMyQuestionsAsync_UserWithoutQuestionsReturnsEmptyHistory()
    {
        await using var db = CreateDbContext();
        var user = await AddUserAsync(db, 123);
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var history = await service.GetMyQuestionsAsync(user.TelegramUserId, 1);

        Assert.True(history.UserExists);
        Assert.True(history.IsEmpty);
        Assert.Empty(history.Items);
        Assert.Equal("📨 У тебя пока нет вопросов.\n\nПоделись своей ссылкой, чтобы получить первый анонимный вопрос.",
            history.EmptyMessage);
    }

    [Fact]
    public async Task GetMyQuestionsAsync_ReturnsOnlyQuestionsForCurrentUser()
    {
        await using var db = CreateDbContext();
        var firstUser = await AddUserAsync(db, 123);
        var secondUser = await AddUserAsync(db, 456);
        await AddQuestionAsync(db, firstUser.Id, "first user's question", DateTime.UtcNow);
        await AddQuestionAsync(db, secondUser.Id, "other user's private question", DateTime.UtcNow);
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var history = await service.GetMyQuestionsAsync(firstUser.TelegramUserId, 1);

        var question = Assert.Single(history.Items);
        Assert.Equal("first user's question", question.Text);
        Assert.DoesNotContain(history.Items, item => item.Text.Contains("private"));
    }

    [Fact]
    public async Task GetMyQuestionsAsync_ExcludesDeletedQuestions()
    {
        await using var db = CreateDbContext();
        var user = await AddUserAsync(db, 123);
        await AddQuestionAsync(db, user.Id, "visible", DateTime.UtcNow);
        await AddQuestionAsync(db, user.Id, "deleted", DateTime.UtcNow, QuestionStatus.Deleted);
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var history = await service.GetMyQuestionsAsync(user.TelegramUserId, 1);

        Assert.Equal("visible", Assert.Single(history.Items).Text);
    }

    [Fact]
    public async Task GetMyQuestionsAsync_SortsOldestFirstSoNewestAppearsAtBottom()
    {
        await using var db = CreateDbContext();
        var user = await AddUserAsync(db, 123);
        var now = DateTime.UtcNow;
        await AddQuestionAsync(db, user.Id, "old", now.AddDays(-1));
        await AddQuestionAsync(db, user.Id, "new", now);
        await AddQuestionAsync(db, user.Id, "middle", now.AddHours(-1));
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var history = await service.GetMyQuestionsAsync(user.TelegramUserId, 1);

        Assert.Equal(["old", "middle", "new"], history.Items.Select(item => item.Text));
    }

    [Fact]
    public async Task GetMyQuestionsAsync_PaginatesFiveQuestionsAtATime()
    {
        await using var db = CreateDbContext();
        var user = await AddUserAsync(db, 123);
        var now = DateTime.UtcNow;
        for (var index = 0; index < 12; index++)
        {
            await AddQuestionAsync(db, user.Id, $"question-{index}", now.AddMinutes(index));
        }
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var firstPage = await service.GetMyQuestionsAsync(user.TelegramUserId, 1);
        var secondPage = await service.GetMyQuestionsAsync(user.TelegramUserId, 2);
        var lastPage = await service.GetMyQuestionsAsync(user.TelegramUserId, 3);

        Assert.Equal(5, firstPage.Items.Count);
        Assert.False(firstPage.HasPrevious);
        Assert.True(firstPage.HasNext);
        Assert.Equal(5, secondPage.Items.Count);
        Assert.True(secondPage.HasPrevious);
        Assert.True(secondPage.HasNext);
        Assert.Equal([6, 7, 8, 9, 10], secondPage.Items.Select(item => item.Number));
        Assert.Equal(2, lastPage.Items.Count);
        Assert.True(lastPage.HasPrevious);
        Assert.False(lastPage.HasNext);
        Assert.Equal([11, 12], lastPage.Items.Select(item => item.Number));
    }

    [Fact]
    public async Task QuestionHistoryItem_DisplaysItsSequentialNumber()
    {
        await using var db = CreateDbContext();
        var user = await AddUserAsync(db, 123);
        await AddQuestionAsync(db, user.Id, "first", DateTime.UtcNow);
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var history = await service.GetMyQuestionsAsync(user.TelegramUserId, 1);

        Assert.Equal(1, Assert.Single(history.Items).Number);
        Assert.Contains("📨 Вопрос #1", history.Items[0].ToDisplayText());
    }

    [Fact]
    public async Task GetMyQuestionsAsync_ChangingPageCannotAccessAnotherUsersQuestions()
    {
        await using var db = CreateDbContext();
        var currentUser = await AddUserAsync(db, 123);
        var otherUser = await AddUserAsync(db, 456);
        var now = DateTime.UtcNow;
        for (var index = 0; index < 7; index++)
        {
            await AddQuestionAsync(db, currentUser.Id, $"mine-{index}", now.AddMinutes(index));
        }
        await AddQuestionAsync(db, otherUser.Id, "other user's question", now.AddDays(1));
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var manipulatedPage = await service.GetMyQuestionsAsync(currentUser.TelegramUserId, int.MaxValue);

        Assert.Equal(2, manipulatedPage.Page);
        Assert.All(manipulatedPage.Items, item => Assert.StartsWith("mine-", item.Text));
        Assert.DoesNotContain(manipulatedPage.Items, item => item.Text.Contains("other"));
    }

    [Fact]
    public async Task QuestionWithoutAnswerDisplaysNoAnswerPlaceholder()
    {
        await using var db = CreateDbContext();
        var user = await AddUserAsync(db, 123);
        await AddQuestionAsync(db, user.Id, "question text", DateTime.UtcNow);
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var history = await service.GetMyQuestionsAsync(user.TelegramUserId, 1);

        Assert.Contains("💬 Мой ответ:\nПока нет ответа", Assert.Single(history.Items).ToDisplayText());
    }

    [Fact]
    public async Task QuestionWithAnswerDisplaysItsAnswer()
    {
        await using var db = CreateDbContext();
        var user = await AddUserAsync(db, 123);
        var question = await AddQuestionAsync(db, user.Id, "question text", DateTime.UtcNow);
        db.Answers.Add(new Answer
        {
            QuestionId = question.Id,
            Text = "my saved answer",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new QuestionService(db, new RecordingQuestionNotifier());

        var history = await service.GetMyQuestionsAsync(user.TelegramUserId, 1);

        Assert.Contains("💬 Мой ответ:\nmy saved answer", Assert.Single(history.Items).ToDisplayText());
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
        string text,
        DateTime createdAt,
        QuestionStatus status = QuestionStatus.New)
    {
        var question = new Question
        {
            ReceiverUserId = receiverUserId,
            Text = text,
            CreatedAt = createdAt,
            Status = status
        };
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        return question;
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
        public Task NotifyAsync(long receiverTelegramUserId, Question question, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
