using AnonymousBot.Data;
using AnonymousBot.Models;
using AnonymousBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Telegram.Bot;
using Telegram.Bot.Types;
using Xunit;
using BotUser = AnonymousBot.Models.User;

namespace AnonymousBot.Tests;

public sealed class PostgreSqlIntegrationTests
{
    [PostgreSqlFact]
    public async Task Migrations_CreateSchemaAndEnforceUniqueConstraints()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using (var migrationContext = database.CreateMigrationContext())
        {
            await migrationContext.Database.MigrateAsync();
            Assert.NotEmpty(await migrationContext.Database.GetAppliedMigrationsAsync());
        }
        await using var db = database.CreateContext();

        db.Users.Add(new BotUser
        {
            TelegramUserId = 781234,
            Token = "postgres-integration-token",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.Equal("postgres-integration-token", await db.Users
            .Where(user => user.TelegramUserId == 781234)
            .Select(user => user.Token)
            .SingleAsync());
        db.Users.Add(new BotUser
        {
            TelegramUserId = 781234,
            Token = "another-token",
            CreatedAt = DateTime.UtcNow
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(exception.GetBaseException());
        Assert.Equal(PostgresErrorCodes.UniqueViolation,
            ((PostgresException)exception.GetBaseException()).SqlState);
    }

    [PostgreSqlFact]
    public async Task Processor_DeduplicatesConcurrentDeliveryUsingAdvisoryLock()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using (var migrationContext = database.CreateMigrationContext())
        {
            await migrationContext.Database.MigrateAsync();
        }

        var handler = new BlockingUpdateHandler();
        var services = new ServiceCollection()
            .AddScoped<ITelegramUpdateHandler>(_ => handler)
            .BuildServiceProvider();
        var factory = new TestDbContextFactory(database.CreateOptions());
        var processor = new TelegramUpdateProcessor(
            factory,
            new PostgreSqlTelegramUpdateLock(factory),
            services.GetRequiredService<IServiceScopeFactory>());
        var client = new TelegramBotClient("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");
        var update = new Update { Id = 700001 };

        var first = processor.ProcessAsync(client, update, CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = processor.ProcessAsync(client, update, CancellationToken.None);
        await Task.Delay(100);
        handler.Release.TrySetResult();

        Assert.True(await first);
        Assert.False(await second);
        Assert.Equal(1, handler.CallCount);

        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.ProcessedTelegramUpdates.CountAsync());
    }

    [PostgreSqlFact]
    public async Task QuestionSubmission_ParallelRequestsRespectSenderRateLimit()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using (var db = database.CreateMigrationContext())
        {
            await db.Database.MigrateAsync();
            var owner = new BotUser
            {
                TelegramUserId = 781235,
                Token = "owner-token",
                CreatedAt = DateTime.UtcNow
            };
            db.Users.Add(owner);
            await db.SaveChangesAsync();
            db.UserSessions.Add(new UserSession
            {
                TelegramUserId = 781236,
                State = UserSessionState.WaitingForAnonymousMessage,
                ReceiverUserId = owner.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            var now = DateTime.UtcNow;
            db.AnonymousMessageAttempts.AddRange(Enumerable.Range(0, 4).Select(_ =>
                new AnonymousMessageAttempt
                {
                    TelegramUserId = 781236,
                    CreatedAt = now
                }));
            await db.SaveChangesAsync();
        }

        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var notifier = new NoOpQuestionNotifier();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = SubmitAfterSignalAsync(new QuestionService(firstDb, notifier), ready.Task);
        var second = SubmitAfterSignalAsync(new QuestionService(secondDb, notifier), ready.Task);
        ready.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == QuestionSubmissionStatus.Sent);
        await using var verify = database.CreateContext();
        Assert.Equal(5, await verify.AnonymousMessageAttempts.CountAsync(
            attempt => attempt.TelegramUserId == 781236));
        Assert.Single(await verify.Questions.ToListAsync());
    }

    private static async Task<QuestionSubmissionResult> SubmitAfterSignalAsync(
        QuestionService service,
        Task ready)
    {
        await ready;
        return await service.SubmitAsync(781236, "integration test question");
    }

    private sealed class BlockingUpdateHandler : ITelegramUpdateHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount;

        public async Task HandleAsync(
            ITelegramBotClient client,
            Update update,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class NoOpQuestionNotifier : IQuestionNotifier
    {
        public Task NotifyAsync(
            long receiverTelegramUserId,
            BotLanguage language,
            Question question,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")))
        {
            Skip = "Set POSTGRES_CONNECTION_STRING to run PostgreSQL integration tests.";
        }
    }
}

internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private readonly string adminConnectionString;
    private readonly string schemaName;

    private PostgreSqlTestDatabase(string adminConnectionString, string schemaName, string connectionString)
    {
        this.adminConnectionString = adminConnectionString;
        this.schemaName = schemaName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<PostgreSqlTestDatabase> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? throw new InvalidOperationException("POSTGRES_CONNECTION_STRING is required.");
        var schemaName = $"bot_test_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var adminConnectionString = builder.ConnectionString;
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
            await command.ExecuteNonQueryAsync();
        }

        builder.SearchPath = schemaName;
        return new PostgreSqlTestDatabase(
            adminConnectionString,
            schemaName,
            builder.ConnectionString);
    }

    public DbContextOptions<AppDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

    public PostgreSqlAppDbContext CreateMigrationContext() =>
        new(new DbContextOptionsBuilder<PostgreSqlAppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    public AppDbContext CreateContext() => new(CreateOptions());

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA \"{schemaName}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }
}
