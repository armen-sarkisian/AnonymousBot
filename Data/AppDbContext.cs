using AnonymousBot.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TelegramMessage> Messages => Set<TelegramMessage>();
}