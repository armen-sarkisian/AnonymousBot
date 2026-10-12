using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Telegram.Bot;
using Telegram.Bot.Types;

public static class TelegramWebhookEndpoint
{
    public const string Path = "/telegram/webhook";
    public const string SecretHeaderName = "X-Telegram-Bot-Api-Secret-Token";
    private const long MaximumRequestSize = 1_048_576;

    public static void MapTelegramWebhook(this WebApplication app, string webhookSecret)
    {
        app.MapPost(Path, async (
            HttpContext context,
            ITelegramBotClient botClient,
            AnonymousBot.Services.ITelegramUpdateProcessor updateProcessor,
            ILoggerFactory loggerFactory,
            ILogger<TelegramUpdateHandler> logger) =>
        {
            if (!context.Request.Headers.TryGetValue(SecretHeaderName, out var suppliedSecrets) ||
                suppliedSecrets.Count != 1 ||
                !SecretsMatch(suppliedSecrets[0], webhookSecret))
            {
                return Results.Unauthorized();
            }

            if (!context.Request.HasJsonContentType())
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature is { IsReadOnly: false })
            {
                bodySizeFeature.MaxRequestBodySize = MaximumRequestSize;
            }

            if (context.Request.ContentLength > MaximumRequestSize)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            Update? update;
            try
            {
                update = await JsonSerializer.DeserializeAsync<Update>(
                    context.Request.Body,
                    Telegram.Bot.JsonBotAPI.Options,
                    context.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            if (update is null ||
                update.Id <= 0 ||
                (update.Message is null) == (update.CallbackQuery is null))
            {
                return Results.BadRequest();
            }

            try
            {
                await updateProcessor.ProcessAsync(botClient, update, context.RequestAborted);
                return Results.Ok();
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                /*loggerFactory.CreateLogger("TelegramWebhook").LogError(
                    "Failed to process Telegram update {UpdateId} ({ExceptionType}).",
                    update.Id,
                    exception.GetType().Name);*/
                logger.LogError(exception, "Failed to process Telegram update {UpdateId}.", update.Id);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });
    }

    private static bool SecretsMatch(string? suppliedSecret, string configuredSecret)
    {
        if (suppliedSecret is null)
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedSecret);
        var expectedBytes = Encoding.UTF8.GetBytes(configuredSecret);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
