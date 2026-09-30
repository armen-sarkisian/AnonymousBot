namespace AnonymousBot.Models;

public sealed class TelegramMessage
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public int TelegramMessageId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; }
}