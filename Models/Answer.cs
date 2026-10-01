namespace AnonymousBot.Models;

public sealed class Answer
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public Question Question { get; set; } = null!;
}
