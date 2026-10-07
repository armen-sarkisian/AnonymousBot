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
		var language = await userService.GetLanguageAsync(
			telegramUser.Id,
			telegramUser.LanguageCode,
			cancellationToken);
		var messages = BotMessages.For(language);

		if (text is not null &&
			(TryGetStartToken(text, out var token) ||
			 IsStartMenuCommand(text, language)))
		{
			if (token is not null)
			{
				if (await userService.IsBlockedAsync(telegramUser.Id, cancellationToken))
				{
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: messages.AccessRestricted,
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
						text: messages.InvalidLink,
						cancellationToken: cancellationToken);
					return;
				}

				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.AnonymousMessagePrompt,
					cancellationToken: cancellationToken);
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.WriteNextMessage,
					cancellationToken: cancellationToken);
				return;
			}

			var result = await userService.StartAsync(
				telegramUser.Id,
				telegramUser.Username,
				telegramUser.FirstName,
				telegramUser.LanguageCode,
				cancellationToken);
			language = result.Language;
			messages = BotMessages.For(language);

			if (!result.HasAccess)
			{
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.AccessRestricted,
					cancellationToken: cancellationToken);
				return;
			}

			await SaveMessageAsync(db, message, text, cancellationToken);
			await client.SendMessage(
				chatId: message.Chat.Id,
				text: messages.Welcome,
				replyMarkup: CreateMainMenuKeyboard(language),
				cancellationToken: cancellationToken);
			return;
		}

		if (await userService.IsBlockedAsync(telegramUser.Id, cancellationToken))
		{
			await client.SendMessage(
				chatId: message.Chat.Id,
				text: messages.AccessRestricted,
				cancellationToken: cancellationToken);
			return;
		}

		if (text is null)
		{
			if (await answerService.IsWaitingForAnswerAsync(telegramUser.Id, cancellationToken))
			{
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.NonTextAnswer,
					cancellationToken: cancellationToken);
			}
			else if (await questionService.IsWaitingForMessageAsync(telegramUser.Id, cancellationToken))
			{
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.NonTextQuestion,
					cancellationToken: cancellationToken);
			}

			return;
		}

		if (IsMyQuestionsCommand(text, language))
		{
			await SendQuestionHistoryAsync(
				client,
				telegramUser.Id,
				message.Chat.Id,
				page: 1,
				cancellationToken,
				language);
			return;
		}

		if (IsLanguageCommand(text, language))
		{
			await client.SendMessage(
				chatId: message.Chat.Id,
				text: messages.ChooseLanguage,
				replyMarkup: CreateLanguageKeyboard(messages),
				cancellationToken: cancellationToken);
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
						text: messages.NoAccessToQuestion,
						cancellationToken: cancellationToken);
					return;
				case AnswerSubmissionStatus.AlreadyAnswered:
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: messages.AlreadyAnswered,
						cancellationToken: cancellationToken);
					return;
				case AnswerSubmissionStatus.EmptyText:
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: messages.EmptyAnswer,
						cancellationToken: cancellationToken);
					return;
				case AnswerSubmissionStatus.TooLong:
					await client.SendMessage(
						chatId: message.Chat.Id,
						text: messages.AnswerTooLong,
						cancellationToken: cancellationToken);
					return;
			}
		}

		if (IsLinkCommand(text, language))
		{
			var botUsername = configuration["Telegram:BotUsername"];
			var link = await userService.GetLinkAsync(telegramUser.Id, botUsername, cancellationToken);
			if (!link.UserExists)
			{
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.StartFirst,
					cancellationToken: cancellationToken);
				return;
			}

			if (link.Link is null)
			{
				logger.LogError("Telegram bot username is not configured.");
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.LinkUnavailable,
					cancellationToken: cancellationToken);
				return;
			}

			await SaveMessageAsync(db, message, text, cancellationToken);
			await client.SendMessage(
				chatId: message.Chat.Id,
				text: $"{messages.PersonalLink(link.Link)}\n\n{messages.LinkDescription}",
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
					text: messages.MessageSent,
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.ReceiverUnavailable:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.ReceiverUnavailable,
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.SelfMessage:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.SelfMessage,
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.EmptyText:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.EmptyMessage,
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.TooLong:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.MessageTooLong,
					cancellationToken: cancellationToken);
				return;
			case QuestionSubmissionStatus.RateLimited:
				await client.SendMessage(
					chatId: message.Chat.Id,
					text: messages.RateLimitExceeded,
					cancellationToken: cancellationToken);
				return;
		}

		await SaveMessageAsync(db, message, text, cancellationToken);
		await client.SendMessage(
			chatId: message.Chat.Id,
			text: messages.Echo(text),
			cancellationToken: cancellationToken);
	}

	private async Task HandleCallbackQueryAsync(
		ITelegramBotClient client,
		CallbackQuery callbackQuery,
		CancellationToken cancellationToken)
	{
		if (TryParseLanguageCallbackData(callbackQuery.Data, out var selectedLanguage))
		{
			await using var languageScope = scopeFactory.CreateAsyncScope();
			var userService = languageScope.ServiceProvider.GetRequiredService<UserService>();
			var saved = await userService.SetLanguageAsync(
				callbackQuery.From.Id,
				selectedLanguage,
				cancellationToken);
			var language = saved
				? selectedLanguage
				: BotMessages.FromTelegramLanguageCode(callbackQuery.From.LanguageCode);
			var languageMessages = BotMessages.For(language);

			await client.AnswerCallbackQuery(
				callbackQuery.Id,
				text: saved ? languageMessages.LanguageChanged : languageMessages.StartFirst,
				showAlert: !saved,
				cancellationToken: cancellationToken);
			if (saved)
			{
				await client.SendMessage(
					chatId: callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id,
					text: languageMessages.LanguageChanged,
					replyMarkup: CreateMainMenuKeyboard(language),
					cancellationToken: cancellationToken);
			}

			return;
		}

		if (TryParseHistoryCallbackData(callbackQuery.Data, out var historyPage))
		{
			await using var historyScope = scopeFactory.CreateAsyncScope();
			var userService = historyScope.ServiceProvider.GetRequiredService<UserService>();
			var language = await userService.GetLanguageAsync(
				callbackQuery.From.Id,
				callbackQuery.From.LanguageCode,
				cancellationToken);
			await SendQuestionHistoryAsync(
				client,
				callbackQuery.From.Id,
				callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id,
				historyPage,
				cancellationToken,
				language);
			await client.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
			return;
		}

		if (!TryParseCallbackData(callbackQuery.Data, out var action, out var questionId))
		{
			var fallbackMessages = BotMessages.For(
				BotMessages.FromTelegramLanguageCode(callbackQuery.From.LanguageCode));
			await client.AnswerCallbackQuery(
				callbackQuery.Id,
				text: fallbackMessages.UnavailableAction,
				cancellationToken: cancellationToken);
			return;
		}

		await using var scope = scopeFactory.CreateAsyncScope();
		var questionService = scope.ServiceProvider.GetRequiredService<QuestionService>();
		var answerService = scope.ServiceProvider.GetRequiredService<AnswerService>();
		var callbackUserService = scope.ServiceProvider.GetRequiredService<UserService>();
		var userLanguage = await callbackUserService.GetLanguageAsync(
			callbackQuery.From.Id,
			callbackQuery.From.LanguageCode,
			cancellationToken);
		var messages = BotMessages.For(userLanguage);

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
					text: messages.NoAccessToQuestion,
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

		if (action == "report")
		{
			var result = await questionService.ReportAsync(
				callbackQuery.From.Id,
				questionId,
				cancellationToken);
			var response = result.Status switch
			{
				QuestionActionStatus.Success => messages.ReportSent,
				QuestionActionStatus.AlreadyReported => messages.ReportAlreadySent,
				QuestionActionStatus.AlreadyDeleted => messages.QuestionAlreadyDeleted,
				_ => messages.NoAccessToQuestion
			};
			await client.AnswerCallbackQuery(
				callbackQuery.Id,
				text: response,
				showAlert: true,
				cancellationToken: cancellationToken);
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
					? messages.QuestionAlreadyDeleted
					: messages.NoAccessToQuestion;
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
				text: messages.QuestionDeleted,
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
				QuestionActionStatus.AlreadyDeleted => messages.QuestionAlreadyDeleted,
				QuestionActionStatus.AlreadyAnswered => messages.AlreadyAnswered,
				_ => messages.NoAccessToQuestion
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
			text: messages.WriteAnswer,
			cancellationToken: cancellationToken);
	}

	private async Task SendQuestionHistoryAsync(
		ITelegramBotClient client,
		long telegramUserId,
		long chatId,
		int page,
		CancellationToken cancellationToken,
		BotLanguage language)
	{
		await using var scope = scopeFactory.CreateAsyncScope();
		var questionService = scope.ServiceProvider.GetRequiredService<QuestionService>();
		var history = await questionService.GetMyQuestionsAsync(
			telegramUserId,
			page,
			cancellationToken,
			language);

		if (!history.UserExists || history.IsEmpty)
		{
			await client.SendMessage(
				chatId: chatId,
				text: history.EmptyMessage ?? BotMessages.For(language).EmptyHistory,
				replyMarkup: CreateMainMenuKeyboard(language),
				cancellationToken: cancellationToken);
			return;
		}

		for (var index = 0; index < history.Items.Count; index++)
		{
			var item = history.Items[index];
			var messageParts = SplitForTelegram(item.ToDisplayText()).ToArray();
			for (var partIndex = 0; partIndex < messageParts.Length; partIndex++)
			{
				var replyMarkup = partIndex == messageParts.Length - 1
					? CreateQuestionHistoryKeyboard(item, history, index == history.Items.Count - 1)
					: null;
				await client.SendMessage(
					chatId: chatId,
					text: messageParts[partIndex],
					replyMarkup: replyMarkup,
					cancellationToken: cancellationToken);
			}
		}
	}

	private static ReplyKeyboardMarkup CreateMainMenuKeyboard(BotLanguage language)
	{
		var messages = BotMessages.For(language);
		return new(new[]
		{
			new[] { new KeyboardButton(messages.StartMenuButton), new KeyboardButton(messages.LinkMenuButton) },
			new[] { new KeyboardButton(messages.MyQuestionsMenuButton), new KeyboardButton(messages.LanguageMenuButton) }
		})
		{
			ResizeKeyboard = true,
			IsPersistent = true
		};
	}

	private static InlineKeyboardMarkup CreateLanguageKeyboard(BotMessages messages) =>
		new(new[]
		{
			InlineKeyboardButton.WithCallbackData(messages.LanguageRussian, "language:set:ru"),
			InlineKeyboardButton.WithCallbackData(messages.LanguageUkrainian, "language:set:uk"),
			InlineKeyboardButton.WithCallbackData(messages.LanguageEnglish, "language:set:en")
		});

	private static InlineKeyboardMarkup? CreateQuestionHistoryKeyboard(
		QuestionHistoryItem item,
		QuestionHistoryPage history,
		bool isLastItem)
	{
		var messages = BotMessages.For(history.Language);
		var rows = new List<InlineKeyboardButton[]>();
		if (item.AnswerText is null)
		{
			rows.Add(QuestionNotificationBuilder.CreateActionButtons(item.Id, history.Language)
				.Select(button => InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackData))
				.ToArray());
		}

		var pageButtons = new List<InlineKeyboardButton>();
		if (isLastItem && history.HasPrevious)
		{
			pageButtons.Add(InlineKeyboardButton.WithCallbackData(
				messages.PreviousPageButton,
				$"my_questions:page:{history.Page - 1}"));
		}

		if (isLastItem && history.HasNext)
		{
			pageButtons.Add(InlineKeyboardButton.WithCallbackData(
				messages.NextPageButton,
				$"my_questions:page:{history.Page + 1}"));
		}

		if (pageButtons.Count > 0)
		{
			rows.Add(pageButtons.ToArray());
		}

		return rows.Count == 0 ? null : new InlineKeyboardMarkup(rows);
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

	private static bool IsMyQuestionsCommand(string text, BotLanguage language)
	{
		var button = BotMessages.For(language).MyQuestionsMenuButton;
		return string.Equals(text.Trim(), button, StringComparison.OrdinalIgnoreCase) ||
		       string.Equals(text.Trim(), "/my_questions", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsStartMenuCommand(string text, BotLanguage language) =>
		string.Equals(
			text.Trim(),
			BotMessages.For(language).StartMenuButton,
			StringComparison.OrdinalIgnoreCase);

	private static bool IsLanguageCommand(string text, BotLanguage language) =>
		string.Equals(
			text.Trim(),
			BotMessages.For(language).LanguageMenuButton,
			StringComparison.OrdinalIgnoreCase);

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

		if ((parts[0] == "question" && parts[1] is "answer" or "delete" or "report") ||
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

	private static bool IsLinkCommand(string text, BotLanguage language)
	{
		var command = text.Split(' ', 2)[0].Split('@')[0];
		return string.Equals(command, "/link", StringComparison.OrdinalIgnoreCase) ||
		       string.Equals(
			       text.Trim(),
			       BotMessages.For(language).LinkMenuButton,
			       StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryParseLanguageCallbackData(string? data, out BotLanguage language)
	{
		language = BotLanguage.Russian;
		var parts = data?.Split(':');
		if (parts is not { Length: 3 } || parts[0] != "language" || parts[1] != "set")
		{
			return false;
		}

		language = parts[2] switch
		{
			"ru" => BotLanguage.Russian,
			"uk" => BotLanguage.Ukrainian,
			"en" => BotLanguage.English,
			_ => BotLanguage.Russian
		};
		return parts[2] is "ru" or "uk" or "en";
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
