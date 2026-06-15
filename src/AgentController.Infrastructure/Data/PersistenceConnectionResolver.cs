using AgentController.Infrastructure.Options;
using Microsoft.Data.Sqlite;

namespace AgentController.Infrastructure.Data;

/// <summary>
/// Resolves and validates persistence connection strings, with special
/// handling for SQLite file-based data sources.
/// </summary>
public static class PersistenceConnectionResolver
{
    /// <summary>
    /// Resolves the connection string from <see cref="PersistenceOptions"/>.
    /// For SQLite, validates that the <c>Data Source</c> value is not a relative
    /// file path and expands <c>~/</c> prefixes to the user home directory.
    /// For non-SQLite providers, the connection string is returned unchanged.
    /// </summary>
    /// <param name="options">Validated <see cref="PersistenceOptions"/>.</param>
    /// <returns>A resolved, safe connection string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The connection string is missing, empty, or contains a relative SQLite
    /// file path.
    /// </exception>
    public static string Resolve(PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connectionString = options.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Persistence connection string is required. " +
                "Configure 'persistence:connectionString' in appsettings.json " +
                "or via an environment variable."
            );
        }

        if (options.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSqlite(connectionString);
        }

        // Non-SQLite providers: pass through unchanged
        return connectionString;
    }

    private static string ResolveSqlite(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new InvalidOperationException(
                $"SQLite connection string must include a Data Source value. " +
                $"Got: '{connectionString}'"
            );
        }

        // Expand ~ to the user home directory
        if (dataSource.StartsWith("~/", StringComparison.Ordinal) || dataSource == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var expanded = dataSource == "~"
                ? home
                : Path.Combine(home, dataSource[2..]);

            builder.DataSource = expanded;
            return builder.ConnectionString;
        }

        // Reject relative paths — must be rooted (starts with / or a drive letter)
        if (!Path.IsPathRooted(dataSource))
        {
            throw new InvalidOperationException(
                $"SQLite Data Source path must be absolute or start with ~/. " +
                $"Relative path '{dataSource}' is not allowed. " +
                $"Use an absolute path or '~/.agent-work-controller/agent-controller.db' instead."
            );
        }

        return builder.ConnectionString;
    }
}
