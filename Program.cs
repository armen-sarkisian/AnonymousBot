using AnonymousBot.Data;
using AnonymousBot.Services;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

var botToken = builder.Configuration["Telegram:BotToken"];
if (string.IsNullOrWhiteSpace(botToken))
{
	throw new InvalidOperationException("Set Telegram:BotToken before starting the application.");
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
	throw new InvalidOperationException("Set the ConnectionStrings:DefaultConnection setting.");
}

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<UserSessionService>();
builder.Services.AddScoped<QuestionService>();
builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(botToken));
builder.Services.AddScoped<IQuestionNotifier, TelegramQuestionNotifier>();
builder.Services.AddHostedService<TelegramPollingService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	await db.Database.MigrateAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
