namespace AnonymousBot.Models;

public sealed class UserSession
{
    public int Id { get; set; }
    public long TelegramUserId { get; set; }
    public UserSessionState State { get; set; }
    public int? ReceiverUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
