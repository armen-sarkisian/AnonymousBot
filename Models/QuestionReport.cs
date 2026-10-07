namespace AnonymousBot.Models;

public sealed class QuestionReport
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public int ReporterUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Question Question { get; set; } = null!;
    public User ReporterUser { get; set; } = null!;
}
