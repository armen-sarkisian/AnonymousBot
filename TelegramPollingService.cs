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
	IConfiguration configuration,
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
		var userSessionService = scope.ServiceProvider.GetRequiredService<UserSessionService>();

		if (TryGetStartToken(text, out var token))
		{
			if (token is not null)
			{
				if (await userService.IsBlockedAsync(telegramUser.Id, cancellationToken))
				{
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: "Доступ к боту ограничен.",
						cancellationToken: cancellationToken);
					return;
				}

				var linkResult = await userSessionService.StartFromLinkAsync(
					telegramUser.Id,
					token,
					cancellationToken);

				if (linkResult.Status != StartLinkStatus.Ready)
				{
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: "❌ Ссылка недействительна или больше не существует.",
						cancellationToken: cancellationToken);
					return;
				}

				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "💬 Ты можешь отправить анонимное сообщение этому пользователю.",
					cancellationToken: cancellationToken);
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "Напиши сообщение следующим сообщением.",
					cancellationToken: cancellationToken);
				return;
			}

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

		if (await userSessionService.ConsumeWaitingMessageAsync(telegramUser.Id, cancellationToken) is not null)
		{
			return;
		}

		if (IsLinkCommand(text))
		{
			var botUsername = configuration["Telegram:BotUsername"];
			var link = await userService.GetLinkAsync(telegramUser.Id, botUsername, cancellationToken);
			if (!link.UserExists)
			{
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "Сначала выполните команду /start.",
					cancellationToken: cancellationToken);
				return;
			}

			if (link.Link is null)
			{
				logger.LogError("Telegram bot username is not configured.");
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "Не удалось создать ссылку. Попробуйте позже.",
					cancellationToken: cancellationToken);
				return;
			}

			await SaveMessageAsync(db, message, text, cancellationToken);
			await client.SendMessage(
				chatId: message.Chat.Id,
				text: $"🔗 Твоя анонимная ссылка:\n{link.Link}\n\nОтправь эту ссылку друзьям, чтобы они могли отправлять тебе анонимные сообщения.",
				cancellationToken: cancellationToken);
			return;
		}

		await SaveMessageAsync(db, message, text, cancellationToken);
		await client.SendMessage(
			chatId: message.Chat.Id,
			text: $"You said: {text}",
			cancellationToken: cancellationToken);
	}

	private static bool TryGetStartToken(string text, out string? token)
	{
		var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		var command = parts.Length == 0 ? string.Empty : parts[0].Split('@')[0];
		if (!string.Equals(command, "/start", StringComparison.OrdinalIgnoreCase))
		{
			token = null;
			return false;
		}

		token = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : null;
		return true;
	}

	private static bool IsLinkCommand(string text)
	{
		var command = text.Split(' ', 2)[0].Split('@')[0];
		return string.Equals(command, "/link", StringComparison.OrdinalIgnoreCase);
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
