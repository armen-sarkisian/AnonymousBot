using System.Net;
using System.Text.RegularExpressions;
using AnonymousBot.Services;

public sealed class WebhookConfiguration(
    string botToken,
    string botUsername,
    Uri webhookUri,
    string webhookSecret)
{
    public string BotToken { get; } = botToken;
    public string BotUsername { get; } = botUsername;
    public Uri WebhookUri { get; } = webhookUri;
    public string WebhookSecret { get; } = webhookSecret;
    public string WebhookUrl => WebhookUri.AbsoluteUri;

    public static WebhookConfiguration Load(IConfiguration configuration)
    {
        var botToken = configuration["BOT_TOKEN"] ?? configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new InvalidOperationException("Set BOT_TOKEN or Telegram:BotToken.");
        }

        var botUsername = configuration["BOT_USERNAME"] ?? configuration["Telegram:BotUsername"];
        if (string.IsNullOrWhiteSpace(botUsername))
        {
            throw new InvalidOperationException("Set BOT_USERNAME or Telegram:BotUsername.");
        }

        var webhookUrl = configuration["WEBHOOK_URL"];
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) ||
            webhookUri.Scheme != Uri.UriSchemeHttps ||
            webhookUri.Port is not (443 or 80 or 88 or 8443) ||
            !string.Equals(webhookUri.AbsolutePath, TelegramWebhookEndpoint.Path, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(webhookUri.Query) ||
            !string.IsNullOrEmpty(webhookUri.Fragment) ||
            !string.IsNullOrEmpty(webhookUri.UserInfo) ||
            IsLocalHost(webhookUri.DnsSafeHost))
        {
            throw new InvalidOperationException(
                $"Set WEBHOOK_URL to a public HTTPS URL ending in {TelegramWebhookEndpoint.Path}.");
        }

        var secret = configuration["WEBHOOK_SECRET"];
        if (!IsValidSecret(secret))
        {
            throw new InvalidOperationException(
                "Set WEBHOOK_SECRET to 1-256 characters using only letters, digits, '_' or '-'.");
        }

        var normalizedBotUsername = botUsername.Trim().TrimStart('@');
        if (normalizedBotUsername.Length == 0)
        {
            throw new InvalidOperationException("Set BOT_USERNAME or Telegram:BotUsername.");
        }

        return new WebhookConfiguration(botToken, normalizedBotUsername, webhookUri, secret!);
    }

    public static bool IsValidSecret(string? secret) =>
        secret is { Length: >= 1 and <= 256 } &&
        Regex.IsMatch(secret, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
        (!host.Contains('.', StringComparison.Ordinal) && !host.Contains(':', StringComparison.Ordinal)) ||
        (IPAddress.TryParse(host, out var address) && IsNonPublicAddress(address));

    private static bool IsNonPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xFE) == 0xFC || bytes[0] == 0xFF;
        }

        return bytes[0] == 0 ||
               bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
               (bytes[0] == 198 && bytes[1] is 18 or 19) ||
               bytes[0] >= 224;
    }
}
