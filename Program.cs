using AnonymousBot.Data;
using AnonymousBot.Services;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

var builder = WebApplication.CreateBuilder(args);

var webhookConfiguration = WebhookConfiguration.Load(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
	throw new InvalidOperationException("Set the ConnectionStrings:DefaultConnection setting.");
}

builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDbContextFactory<PostgreSqlAppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton(webhookConfiguration);
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<UserSessionService>();
builder.Services.AddScoped<QuestionService>();
builder.Services.AddScoped<AnswerService>();
builder.Services.AddScoped<ITelegramUpdateHandler, TelegramUpdateHandler>();
builder.Services.AddScoped<ITelegramUpdateProcessor, TelegramUpdateProcessor>();
builder.Services.AddSingleton<ITelegramUpdateLock, PostgreSqlTelegramUpdateLock>();
builder.Services.AddSingleton<ITelegramBotClient>(
	_ => new TelegramBotClient(webhookConfiguration.BotToken));
builder.Services.AddScoped<IQuestionNotifier, TelegramQuestionNotifier>();
builder.Services.AddScoped<IAnswerNotifier, TelegramAnswerNotifier>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
	var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PostgreSqlAppDbContext>>();
	await using var db = await dbFactory.CreateDbContextAsync();
	await db.Database.MigrateAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapTelegramWebhook(webhookConfiguration.WebhookSecret);

await app.StartAsync();
try
{
	var botClient = app.Services.GetRequiredService<ITelegramBotClient>();
	await botClient.SetWebhook(
		webhookConfiguration.WebhookUrl,
		secretToken: webhookConfiguration.WebhookSecret,
		allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
		cancellationToken: app.Lifetime.ApplicationStopping);
}
catch
{
	await app.StopAsync();
	throw new InvalidOperationException(
		"Could not register the Telegram webhook; verify configuration and outbound connectivity.");
}

await app.WaitForShutdownAsync();
