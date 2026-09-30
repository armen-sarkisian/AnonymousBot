using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TelegramMessage> Messages => Set<TelegramMessage>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(user => user.TelegramUserId).IsRequired();
            entity.Property(user => user.Username).HasMaxLength(32);
            entity.Property(user => user.FirstName).HasMaxLength(64);
            entity.Property(user => user.Token).IsRequired().HasMaxLength(128);
            entity.HasIndex(user => user.TelegramUserId).IsUnique();
            entity.HasIndex(user => user.Token).IsUnique();
        });
    }
}