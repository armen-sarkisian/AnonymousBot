namespace AnonymousBot.Models;

public sealed class Question
{
    public int Id { get; set; }
    public int ReceiverUserId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public QuestionStatus Status { get; set; }

    public User ReceiverUser { get; set; } = null!;
}
