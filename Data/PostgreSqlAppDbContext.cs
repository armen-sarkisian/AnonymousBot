using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Data;

public sealed class PostgreSqlAppDbContext(
    DbContextOptions<PostgreSqlAppDbContext> options) : DbContext(options)
{
    public DbSet<TelegramMessage> Messages => Set<TelegramMessage>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<QuestionReport> QuestionReports => Set<QuestionReport>();
    public DbSet<AnonymousMessageAttempt> AnonymousMessageAttempts => Set<AnonymousMessageAttempt>();
    public DbSet<ProcessedTelegramUpdate> ProcessedTelegramUpdates => Set<ProcessedTelegramUpdate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        AppDbContext.ConfigureModel(modelBuilder);
}
