using AnonymousBot.Data;
using AnonymousBot.Models;
using AnonymousBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

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
		if (update.CallbackQuery is { } callbackQuery)
		{
			await HandleCallbackQueryAsync(client, callbackQuery, cancellationToken);
			return;
		}

		if (update.Message is not { } message || message.From is not { } telegramUser)
		{
			return;
		}

		var text = message.Text;
		await using var scope = scopeFactory.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
		var userService = scope.ServiceProvider.GetRequiredService<UserService>();
		var userSessionService = scope.ServiceProvider.GetRequiredService<UserSessionService>();
		var questionService = scope.ServiceProvider.GetRequiredService<QuestionService>();
		var answerService = scope.ServiceProvider.GetRequiredService<AnswerService>();

		if (text is not null && TryGetStartToken(text, out var token))
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
				replyMarkup: CreateMainMenuKeyboard(),
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

		if (text is null)
		{
			if (await answerService.IsWaitingForAnswerAsync(telegramUser.Id, cancellationToken))
			{
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "❌ Ответ пока можно отправить только текстом.",
					cancellationToken: cancellationToken);
			}
			else if (await questionService.IsWaitingForMessageAsync(telegramUser.Id, cancellationToken))
			{
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "❌ Сейчас можно отправлять только текстовые сообщения.",
					cancellationToken: cancellationToken);
			}

			return;
		}

		if (IsMyQuestionsCommand(text))
		{
			await SendQuestionHistoryAsync(
				client,
				telegramUser.Id,
				message.Chat.Id,
				page: 1,
				cancellationToken);
			return;
		}

		if (await answerService.IsWaitingForAnswerAsync(telegramUser.Id, cancellationToken))
		{
			var answerResult = await answerService.SubmitAsync(
				telegramUser.Id,
				text,
				cancellationToken);

			switch (answerResult.Status)
			{
				case AnswerSubmissionStatus.Saved:
					return;
				case AnswerSubmissionStatus.QuestionUnavailable:
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: "❌ У тебя нет доступа к этому вопросу.",
						cancellationToken: cancellationToken);
					return;
				case AnswerSubmissionStatus.AlreadyAnswered:
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: "На этот вопрос уже сохранён ответ.",
						cancellationToken: cancellationToken);
					return;
				case AnswerSubmissionStatus.EmptyText:
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: "❌ Ответ не может быть пустым.",
						cancellationToken: cancellationToken);
					return;
				case AnswerSubmissionStatus.TooLong:
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: "❌ Ответ слишком длинный. Сократи его и попробуй снова.",
						cancellationToken: cancellationToken);
					return;
			}
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

		var submission = await questionService.SubmitAsync(
			telegramUser.Id,
			text,
			cancellationToken);

		switch (submission.Status)
		{
			case QuestionSubmissionStatus.Sent:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "✅ Сообщение отправлено анонимно.",
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.ReceiverUnavailable:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "❌ Этот пользователь сейчас не принимает сообщения.",
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.SelfMessage:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "❌ Нельзя отправить анонимное сообщение самому себе.",
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.EmptyText:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "❌ Сообщение не может быть пустым.",
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.TooLong:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: "❌ Сообщение слишком длинное. Сократи его и попробуй снова.",
					cancellationToken: cancellationToken);
				return;
		}

		await SaveMessageAsync(db, message, text, cancellationToken);
		await client.SendMessage(
			chatId: message.Chat.Id,
			text: $"You said: {text}",
			cancellationToken: cancellationToken);
	}

	private async Task HandleCallbackQueryAsync(
		ITelegramBotClient client,
		CallbackQuery callbackQuery,
		CancellationToken cancellationToken)
	{
		if (TryParseHistoryCallbackData(callbackQuery.Data, out var historyPage))
		{
			await SendQuestionHistoryAsync(
				client,
				callbackQuery.From.Id,
				callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id,
				historyPage,
				cancellationToken);
			await client.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
			return;
		}

		if (!TryParseCallbackData(callbackQuery.Data, out var action, out var questionId))
		{
			await client.AnswerCallbackQuery(
				callbackQuery.Id,
				text: "Это действие недоступно.",
				cancellationToken: cancellationToken);
			return;
		}

		await using var scope = scopeFactory.CreateAsyncScope();
		var questionService = scope.ServiceProvider.GetRequiredService<QuestionService>();
		var answerService = scope.ServiceProvider.GetRequiredService<AnswerService>();

		if (action == "copy")
		{
			var draftResult = await answerService.GetDraftAsync(
				callbackQuery.From.Id,
				questionId,
				cancellationToken);
			if (draftResult.Status != AnswerDraftStatus.Ready || draftResult.Notification is null)
			{
				await client.AnswerCallbackQuery(
					callbackQuery.Id,
					text: "❌ У тебя нет доступа к этому вопросу.",
					showAlert: true,
					cancellationToken: cancellationToken);
				return;
			}

			await client.SendMessage(
				chatId: draftResult.Notification.OwnerTelegramUserId,
				text: draftResult.Notification.DraftText,
				cancellationToken: cancellationToken);
			await client.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
			return;
		}

		if (action == "delete")
		{
			var result = await questionService.DeleteAsync(
				callbackQuery.From.Id,
				questionId,
				cancellationToken);
			if (result.Status != QuestionActionStatus.Success)
			{
				var error = result.Status == QuestionActionStatus.AlreadyDeleted
					? "Вопрос уже удалён."
					: "❌ У тебя нет доступа к этому вопросу.";
				await client.AnswerCallbackQuery(
					callbackQuery.Id,
					text: error,
					showAlert: true,
					cancellationToken: cancellationToken);
				return;
			}

			if (callbackQuery.Message is { } deletedMessage)
			{
				await client.EditMessageReplyMarkup(
					chatId: deletedMessage.Chat.Id,
					messageId: deletedMessage.MessageId,
					replyMarkup: null,
					cancellationToken: cancellationToken);
			}

			await client.AnswerCallbackQuery(
				callbackQuery.Id,
				text: "🗑 Вопрос удалён.",
				cancellationToken: cancellationToken);
			return;
		}

		var answerStatus = await answerService.BeginAnswerAsync(
			callbackQuery.From.Id,
			questionId,
			cancellationToken);
		if (answerStatus != QuestionActionStatus.Success)
		{
			var error = answerStatus switch
			{
				QuestionActionStatus.AlreadyDeleted => "Вопрос уже удалён.",
				QuestionActionStatus.AlreadyAnswered => "На этот вопрос уже сохранён ответ.",
				_ => "❌ У тебя нет доступа к этому вопросу."
			};
			await client.AnswerCallbackQuery(
				callbackQuery.Id,
				text: error,
				showAlert: true,
				cancellationToken: cancellationToken);
			return;
		}

		await client.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
		await client.SendMessage(
			chatId: callbackQuery.From.Id,
			text: "✍️ Напиши свой ответ на этот вопрос.",
			cancellationToken: cancellationToken);
	}

	private async Task SendQuestionHistoryAsync(
		ITelegramBotClient client,
		long telegramUserId,
		long chatId,
		int page,
		CancellationToken cancellationToken)
	{
		await using var scope = scopeFactory.CreateAsyncScope();
		var questionService = scope.ServiceProvider.GetRequiredService<QuestionService>();
		var history = await questionService.GetMyQuestionsAsync(telegramUserId, page, cancellationToken);

		if (!history.UserExists || history.IsEmpty)
		{
			await client.SendMessage(
				chatId: chatId,
				text: history.EmptyMessage ?? QuestionService.EmptyHistoryText,
				replyMarkup: CreateMainMenuKeyboard(),
				cancellationToken: cancellationToken);
			return;
		}

		var displayParts = history.Items
			.SelectMany(item => SplitForTelegram(item.ToDisplayText()))
			.ToArray();
		for (var index = 0; index < displayParts.Length; index++)
		{
			var replyMarkup = index == displayParts.Length - 1
				? CreateHistoryKeyboard(history)
				: null;
			await client.SendMessage(
				chatId: chatId,
				text: displayParts[index],
				replyMarkup: replyMarkup,
				cancellationToken: cancellationToken);
		}
	}

	private static ReplyKeyboardMarkup CreateMainMenuKeyboard() =>
		new(new[] { new KeyboardButton("📨 Мои вопросы") })
		{
			ResizeKeyboard = true,
			IsPersistent = true
		};

	private static InlineKeyboardMarkup? CreateHistoryKeyboard(QuestionHistoryPage history)
	{
		var buttons = new List<InlineKeyboardButton>();
		if (history.HasPrevious)
		{
			buttons.Add(InlineKeyboardButton.WithCallbackData(
				"⬅️ Назад",
				$"my_questions:page:{history.Page - 1}"));
		}

		if (history.HasNext)
		{
			buttons.Add(InlineKeyboardButton.WithCallbackData(
				"➡️ Далее",
				$"my_questions:page:{history.Page + 1}"));
		}

		return buttons.Count == 0 ? null : new InlineKeyboardMarkup(buttons);
	}

	private static IEnumerable<string> SplitForTelegram(string text)
	{
		for (var offset = 0; offset < text.Length;)
		{
			var length = Math.Min(QuestionNotificationBuilder.TelegramMessageLimit, text.Length - offset);
			if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
			{
				length--;
			}

			yield return text.Substring(offset, length);
			offset += length;
		}
	}

	private static bool IsMyQuestionsCommand(string text) =>
		string.Equals(text.Trim(), "📨 Мои вопросы", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(text.Trim(), "/my_questions", StringComparison.OrdinalIgnoreCase);

	private static bool TryParseHistoryCallbackData(string? data, out int page)
	{
		page = 0;
		var parts = data?.Split(':');
		return parts is { Length: 3 } &&
		       parts[0] == "my_questions" &&
		       parts[1] == "page" &&
		       int.TryParse(parts[2], out page) &&
		       page > 0;
	}

	private static bool TryParseCallbackData(string? data, out string action, out int questionId)
	{
		action = string.Empty;
		questionId = 0;
		var parts = data?.Split(':');
		if (parts is not { Length: 3 } ||
			!int.TryParse(parts[2], out questionId) ||
			questionId <= 0)
		{
			return false;
		}

		if ((parts[0] == "question" && parts[1] is "answer" or "delete") ||
			(parts[0] == "answer" && parts[1] == "copy"))
		{
			action = parts[1] == "answer" ? "begin-answer" : parts[1];
			return true;
		}

		return false;
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
