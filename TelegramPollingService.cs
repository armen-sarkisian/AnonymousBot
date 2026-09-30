using AnonymousBot.Data;
using AnonymousBot.Models;
using AnonymousBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

public sealed class TelegramPollingService(
	ITelegramBotClient botClient,
	IServiceScopeFactory scopeFactory,
	ILogger<TelegramPollingService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await botClient.DeleteWebhook(cancellationToken: stoppingToken);

		botClient.StartReceiving(
			updateHandler: HandleUpdateAsync,
			errorHandler: HandlePollingErrorAsync,
			receiverOptions: new ReceiverOptions { AllowedUpdates = [] },
			cancellationToken: stoppingToken);

		await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
	}

	private async Task HandleUpdateAsync(
		ITelegramBotClient client,
		Update update,
		CancellationToken cancellationToken)
	{
		if (update.Message?.Text is not { } text)
		{
			return;
		}

		var message = update.Message;
		if (message.From is not { } telegramUser)
		{
			return;
		}

		await using var scope = scopeFactory.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
		var userService = scope.ServiceProvider.GetRequiredService<UserService>();

		if (IsStartCommand(text))
		{
			var result = await userService.StartAsync(
				telegramUser.Id,
				telegramUser.Username,
				telegramUser.FirstName,
				cancellationToken);

			if (!result.HasAccess)
			{
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "Доступ к боту ограничен.",
					cancellationToken: cancellationToken);
				return;
			}

			await SaveMessageAsync(db, message, text, cancellationToken);
			await client.SendMessage(
				chatId: message.Chat.Id,
				text: "Добро пожаловать!",
				cancellationToken: cancellationToken);
			return;
		}

		if (await userService.IsBlockedAsync(telegramUser.Id, cancellationToken))
		{
			await client.SendMessage(
				chatId: message.Chat.Id,
				text: "Доступ к боту ограничен.",
				cancellationToken: cancellationToken);
			return;
		}

		await SaveMessageAsync(db, message, text, cancellationToken);
		await client.SendMessage(
			chatId: message.Chat.Id,
			text: $"You said: {text}",
			cancellationToken: cancellationToken);
	}

	private static bool IsStartCommand(string text)
	{
		var command = text.Split(' ', 2)[0].Split('@')[0];
		return string.Equals(command, "/start", StringComparison.OrdinalIgnoreCase);
	}

	private static async Task SaveMessageAsync(
		AppDbContext db,
		Telegram.Bot.Types.Message message,
		string text,
		CancellationToken cancellationToken)
	{
		db.Messages.Add(new TelegramMessage
		{
			ChatId = message.Chat.Id,
			TelegramMessageId = message.MessageId,
			Text = text,
			ReceivedAtUtc = DateTime.UtcNow
		});
		await db.SaveChangesAsync(cancellationToken);
	}

	private Task HandlePollingErrorAsync(
		ITelegramBotClient client,
		Exception exception,
		CancellationToken cancellationToken)
	{
		logger.LogError(exception, "Telegram long polling failed.");
		return Task.CompletedTask;
	}
}
