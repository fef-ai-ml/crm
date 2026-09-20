using Microsoft.Data.SqlClient;

namespace Crm.Profiles.Infrastructure.Persistence;

internal static class SqlErrors
{
    /// <summary>2601: duplicate key row in a unique index. 2627: unique or primary-key constraint violation.</summary>
    public static bool IsUniqueViolation(SqlException exception) => exception.Number is 2601 or 2627;
}
