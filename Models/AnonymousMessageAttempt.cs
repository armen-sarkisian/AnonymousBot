namespace AnonymousBot.Models;

public sealed class AnonymousMessageAttempt
{
    public int Id { get; set; }
    public long TelegramUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}
