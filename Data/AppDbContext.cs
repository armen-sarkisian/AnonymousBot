using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TelegramMessage> Messages => Set<TelegramMessage>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<QuestionReport> QuestionReports => Set<QuestionReport>();
    public DbSet<AnonymousMessageAttempt> AnonymousMessageAttempts => Set<AnonymousMessageAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(user => user.TelegramUserId).IsRequired();
            entity.Property(user => user.Username).HasMaxLength(32);
            entity.Property(user => user.FirstName).HasMaxLength(64);
            entity.Property(user => user.Token).IsRequired().HasMaxLength(128);
            entity.Property(user => user.Language)
                .HasConversion<int>()
                .HasDefaultValue(BotLanguage.Russian);
            entity.HasIndex(user => user.TelegramUserId).IsUnique();
            entity.HasIndex(user => user.Token).IsUnique();
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasIndex(session => session.TelegramUserId).IsUnique();
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(session => session.ReceiverUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Question>(entity =>
        {
            entity.Property(question => question.Text).IsRequired();
            entity.Property(question => question.Status).HasConversion<int>();
            entity.HasOne(question => question.ReceiverUser)
                .WithMany(user => user.Questions)
                .HasForeignKey(question => question.ReceiverUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Answer>(entity =>
        {
            entity.Property(answer => answer.Text).IsRequired();
            entity.HasIndex(answer => answer.QuestionId).IsUnique();
            entity.HasOne(answer => answer.Question)
                .WithOne(question => question.Answer)
                .HasForeignKey<Answer>(answer => answer.QuestionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QuestionReport>(entity =>
        {
            entity.HasIndex(report => new { report.QuestionId, report.ReporterUserId }).IsUnique();
            entity.HasOne(report => report.Question)
                .WithMany(question => question.Reports)
                .HasForeignKey(report => report.QuestionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(report => report.ReporterUser)
                .WithMany(user => user.QuestionReports)
                .HasForeignKey(report => report.ReporterUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnonymousMessageAttempt>(entity =>
        {
            entity.HasIndex(attempt => new { attempt.TelegramUserId, attempt.CreatedAt });
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasOne<Question>()
                .WithMany()
                .HasForeignKey(session => session.QuestionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}