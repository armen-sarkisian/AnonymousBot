using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using AnonymousBot.Data;
using Microsoft.EntityFrameworkCore;

namespace AnonymousBot.Services;

public sealed class PostgreSqlTelegramUpdateLock(
    IDbContextFactory<AppDbContext> dbContextFactory) : ITelegramUpdateLock
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    public async Task<IAsyncDisposable> AcquireAsync(
        long updateId,
        CancellationToken cancellationToken)
    {
        var lockKey = PostgreSqlAdvisoryLockKeys.ForTelegramUpdate(updateId);
        var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            var connection = db.Database.GetDbConnection();
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryAcquireAsync(connection, lockKey, cancellationToken))
                {
                    return new PostgreSqlAdvisoryLockLease(db, lockKey);
                }

                var remaining = LockTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for the Telegram update lock for update {updateId}.");
                }

                await Task.Delay(
                    remaining < RetryInterval ? remaining : RetryInterval,
                    cancellationToken);
            }
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> TryAcquireAsync(
        DbConnection connection,
        long lockKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@lockKey);";
        command.CommandTimeout = 5;
        AddLockKeyParameter(command, lockKey);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    internal static void AddLockKeyParameter(DbCommand command, long lockKey)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lockKey";
        parameter.Value = lockKey;
        command.Parameters.Add(parameter);
    }

    private sealed class PostgreSqlAdvisoryLockLease(
        AppDbContext db,
        long lockKey) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                var command = db.Database.GetDbConnection().CreateCommand();
                await using (command)
                {
                    command.CommandText = "SELECT pg_advisory_unlock(@lockKey);";
                    command.CommandTimeout = 5;
                    AddLockKeyParameter(command, lockKey);
                    var released = Convert.ToBoolean(await command.ExecuteScalarAsync());
                    if (!released)
                    {
                        throw new InvalidOperationException(
                            "PostgreSQL reported that the Telegram update advisory lock was not held.");
                    }
                }
            }
            catch (DbException releaseException)
            {
                try
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT pg_advisory_unlock_all();";
                    command.CommandTimeout = 5;
                    await command.ExecuteScalarAsync();
                }
                catch (Exception fallbackException)
                {
                    throw new AggregateException(
                        "Could not release the PostgreSQL Telegram update advisory lock.",
                        releaseException,
                        fallbackException);
                }

                ExceptionDispatchInfo.Capture(releaseException).Throw();
                throw;
            }
            finally
            {
                try
                {
                    await db.Database.CloseConnectionAsync();
                }
                finally
                {
                    await db.DisposeAsync();
                }
            }
        }
    }
}
