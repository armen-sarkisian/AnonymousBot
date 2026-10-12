namespace AnonymousBot.Models;

public sealed class ProcessedTelegramUpdate
{
    public long UpdateId { get; set; }
    public DateTime ProcessedAt { get; set; }
}
