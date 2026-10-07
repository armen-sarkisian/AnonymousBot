namespace AnonymousBot.Models;

public sealed class User
{
    public int Id { get; set; }
    public long TelegramUserId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsBlocked { get; set; }
    public BotLanguage Language { get; set; } = BotLanguage.Russian;
    public ICollection<Question> Questions { get; } = new List<Question>();
    public ICollection<QuestionReport> QuestionReports { get; } = new List<QuestionReport>();
}
