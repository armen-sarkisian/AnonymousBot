using System.Collections.Concurrent;
using System.Net;
using AnonymousBot.Data;
using AnonymousBot.Models;
using AnonymousBot.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;
using Xunit;

namespace AnonymousBot.Tests;

public sealed class TelegramWebhookTests
{
    private const string Secret = "valid_webhook-secret_0123456789";
    private const string ValidUpdateJson =
        """{"update_id":42,"message":{"message_id":1,"date":1690000000,"chat":{"id":123,"type":"private"},"from":{"id":123,"is_bot":false,"first_name":"Test"},"text":"/start"}}""";

    [Fact]
    public async Task PostWebhook_ValidSecretAndUpdateProcessesAndAcknowledges()
    {
        var processor = new RecordingUpdateProcessor();
        await using var app = await StartAppAsync(processor);
        using var client = CreateClient(app);
        using var request = CreateWebhookRequest(Secret, ValidUpdateJson);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(42, Assert.Single(processor.Updates).Id);
    }

    [Fact]
    public async Task PostWebhook_WrongSecretIsRejectedBeforeParsingBody()
    {
        var processor = new RecordingUpdateProcessor();
        await using var app = await StartAppAsync(processor);
        using var client = CreateClient(app);
        using var request = CreateWebhookRequest("wrong", "{invalid-json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(processor.Updates);
    }

    [Fact]
    public async Task PostWebhook_MissingSecretIsRejectedBeforeParsingBody()
    {
        var processor = new RecordingUpdateProcessor();
        await using var app = await StartAppAsync(processor);
        using var client = CreateClient(app);
        using var request = CreateWebhookRequest(null, "{invalid-json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(processor.Updates);
    }

    [Fact]
    public async Task PostWebhook_InvalidJsonReturnsBadRequest()
    {
        var processor = new RecordingUpdateProcessor();
        await using var app = await StartAppAsync(processor);
        using var client = CreateClient(app);
        using var request = CreateWebhookRequest(Secret, "{invalid-json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(processor.Updates);
    }

    [Fact]
    public async Task PostWebhook_UpdateWithoutSupportedPayloadReturnsBadRequest()
    {
        var processor = new RecordingUpdateProcessor();
        await using var app = await StartAppAsync(processor);
        using var client = CreateClient(app);
        using var request = CreateWebhookRequest(Secret, """{"update_id":42}""");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(processor.Updates);
    }

    [Fact]
    public async Task PostWebhook_ProcessingFailureReturnsServerErrorForRetry()
    {
        var processor = new RecordingUpdateProcessor
        {
            OnProcess = _ => throw new InvalidOperationException("test failure")
        };
        await using var app = await StartAppAsync(processor);
        using var client = CreateClient(app);
        using var request = CreateWebhookRequest(Secret, ValidUpdateJson);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(processor.Updates);
    }

    [Fact]
    public async Task PostWebhook_DoesNotAcknowledgeBeforeProcessingCompletes()
    {
        var releaseProcessing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new RecordingUpdateProcessor
        {
            OnProcess = _ => releaseProcessing.Task
        };
        await using var app = await StartAppAsync(processor);
        using var client = CreateClient(app);
        using var request = CreateWebhookRequest(Secret, ValidUpdateJson);

        var responseTask = client.SendAsync(request);
        await Task.Delay(100);
        Assert.False(responseTask.IsCompleted);
        releaseProcessing.SetResult();
        using var response = await responseTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("bad.dot")]
    [InlineData("é")]
    public void WebhookConfiguration_RejectsMissingOrInvalidSecret(string? secret)
    {
        Assert.Throws<InvalidOperationException>(() => WebhookConfiguration.Load(
            BuildConfiguration(secret, "https://example.com/telegram/webhook")));
    }

    [Fact]
    public void WebhookConfiguration_RejectsLocalOrNonHttpsUrls()
    {
        Assert.Throws<InvalidOperationException>(() => WebhookConfiguration.Load(
            BuildConfiguration(Secret, "http://example.com/telegram/webhook")));
        Assert.Throws<InvalidOperationException>(() => WebhookConfiguration.Load(
            BuildConfiguration(Secret, "https://localhost/telegram/webhook")));
        Assert.Throws<InvalidOperationException>(() => WebhookConfiguration.Load(
            BuildConfiguration(Secret, "https://example.com/")));
        Assert.Throws<InvalidOperationException>(() => WebhookConfiguration.Load(
            BuildConfiguration(Secret, "https://127.0.0.1/telegram/webhook")));
        Assert.Throws<InvalidOperationException>(() => WebhookConfiguration.Load(
            BuildConfiguration(Secret, "https://example.com:444/telegram/webhook")));
    }

    [Fact]
    public async Task UpdateProcessor_DeduplicatesSuccessfullyProcessedUpdates()
    {
        await using var provider = CreateProcessorProvider(
            "dedup-" + Guid.NewGuid(),
            new InMemoryTelegramUpdateLock(),
            new RecordingUpdateHandler());
        var processor = provider.GetRequiredService<ITelegramUpdateProcessor>();
        var update = new Update { Id = 73 };
        var botClient = CreateBotClient();

        var first = await processor.ProcessAsync(botClient, update, CancellationToken.None);
        var duplicate = await processor.ProcessAsync(botClient, update, CancellationToken.None);

        Assert.True(first);
        Assert.False(duplicate);
        Assert.Equal(1, ((RecordingUpdateHandler)provider.GetRequiredService<ITelegramUpdateHandler>()).CallCount);
        await using var db = await provider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await db.ProcessedTelegramUpdates.CountAsync());
    }

    [Fact]
    public async Task UpdateProcessor_DoesNotMarkUpdateWhenHandlerFailsAndAllowsRetry()
    {
        var handler = new RecordingUpdateHandler { FailNext = true };
        await using var provider = CreateProcessorProvider(
            "retry-" + Guid.NewGuid(),
            new InMemoryTelegramUpdateLock(),
            handler);
        var processor = provider.GetRequiredService<ITelegramUpdateProcessor>();
        var update = new Update { Id = 74 };
        var botClient = CreateBotClient();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(botClient, update, CancellationToken.None));

        await using (var db = await provider.GetRequiredService<IDbContextFactory<AppDbContext>>()
                         .CreateDbContextAsync())
        {
            Assert.Empty(await db.ProcessedTelegramUpdates.ToListAsync());
        }

        Assert.True(await processor.ProcessAsync(botClient, update, CancellationToken.None));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task UpdateProcessor_ConcurrentDuplicateUpdatesOnlyRunHandlerOnce()
    {
        var handler = new RecordingUpdateHandler { Delay = TimeSpan.FromMilliseconds(100) };
        await using var provider = CreateProcessorProvider(
            "parallel-" + Guid.NewGuid(),
            new InMemoryTelegramUpdateLock(),
            handler);
        var processor = provider.GetRequiredService<ITelegramUpdateProcessor>();
        var update = new Update { Id = 75 };
        var botClient = CreateBotClient();

        var results = await Task.WhenAll(
            processor.ProcessAsync(botClient, update, CancellationToken.None),
            processor.ProcessAsync(botClient, update, CancellationToken.None));

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(1, handler.CallCount);
        await using var db = await provider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await db.ProcessedTelegramUpdates.CountAsync());
    }

    private static IConfiguration BuildConfiguration(
        string? secret,
        string webhookUrl = "https://example.com/telegram/webhook")
    {
        var values = new Dictionary<string, string?>
        {
            ["BOT_TOKEN"] = "test-token",
            ["BOT_USERNAME"] = "test_bot",
            ["WEBHOOK_URL"] = webhookUrl,
            ["WEBHOOK_SECRET"] = secret
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<WebApplication> StartAppAsync(RecordingUpdateProcessor processor)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITelegramBotClient>(CreateBotClient());
        builder.Services.AddSingleton<ITelegramUpdateProcessor>(processor);
        var app = builder.Build();
        app.MapTelegramWebhook(Secret);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static HttpRequestMessage CreateWebhookRequest(string? secret, string content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TelegramWebhookEndpoint.Path)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
        };
        if (secret is not null)
        {
            request.Headers.TryAddWithoutValidation(TelegramWebhookEndpoint.SecretHeaderName, secret);
        }

        return request;
    }

    private static TelegramBotClient CreateBotClient() => new("123456:test_token");

    private static ServiceProvider CreateProcessorProvider(
        string databaseName,
        ITelegramUpdateLock updateLock,
        RecordingUpdateHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<ITelegramUpdateLock>(updateLock);
        services.AddSingleton<ITelegramUpdateHandler>(handler);
        services.AddSingleton<ITelegramUpdateProcessor, TelegramUpdateProcessor>();
        return services.BuildServiceProvider();
    }

    private sealed class RecordingUpdateProcessor : ITelegramUpdateProcessor
    {
        public ConcurrentBag<Update> Updates { get; } = [];
        public Func<Update, Task>? OnProcess { get; init; }

        public async Task<bool> ProcessAsync(
            ITelegramBotClient client,
            Update update,
            CancellationToken cancellationToken)
        {
            Updates.Add(update);
            if (OnProcess is not null)
            {
                await OnProcess(update);
            }

            return true;
        }
    }

    private sealed class RecordingUpdateHandler : ITelegramUpdateHandler
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);
        public bool FailNext { get; set; }
        public TimeSpan Delay { get; init; }

        public async Task HandleAsync(
            ITelegramBotClient client,
            Update update,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("test handler failure");
            }
        }
    }

    private sealed class InMemoryTelegramUpdateLock : ITelegramUpdateLock
    {
        private readonly ConcurrentDictionary<long, SemaphoreSlim> locks = new();

        public async Task<IAsyncDisposable> AcquireAsync(long updateId, CancellationToken cancellationToken)
        {
            var semaphore = locks.GetOrAdd(updateId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            return new SemaphoreLease(semaphore);
        }

        private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                semaphore.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
