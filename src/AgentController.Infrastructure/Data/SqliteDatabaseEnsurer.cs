using Microsoft.Data.Sqlite;

namespace AgentController.Infrastructure.Data;

/// <summary>
/// Ensures that the parent directory for SQLite file-based databases exists.
/// Used by the migration runner (and potentially other startup code) before
/// opening a file-based SQLite connection.
/// </summary>
public static class SqliteDatabaseEnsurer
{
    /// <summary>
    /// Creates the parent directory for a SQLite file-based database when
    /// the directory does not already exist.
    /// </summary>
    /// <param name="connectionString">A resolved SQLite connection string.</param>
    /// <returns>
    /// The full path of the created directory, or <c>null</c> when no
    /// directory was created (directory already exists, in-memory database,
    /// relative path, or empty connection string).
    /// </returns>
    public static string? EnsureDirectory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource))
            return null;

        // Skip special SQLite data sources like ":memory:"
        if (dataSource == ":memory:" || !Path.IsPathRooted(dataSource))
            return null;

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return directory;
        }

        return null;
    }
}
