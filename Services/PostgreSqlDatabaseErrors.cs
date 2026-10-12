using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AnonymousBot.Services;

internal static class PostgreSqlDatabaseErrors
{
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.GetBaseException() is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };
}
