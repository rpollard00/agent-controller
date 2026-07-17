using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations
{
    /// <summary>
    /// Add Project column to Repositories table and repoint RepositoryHostConnectionKey
    /// to the unified Connections table.
    /// 
    /// Project is backfilled from the previously-referenced RepositoryHostConnection.Project.
    /// RepositoryHostConnectionKey is retargeted to the unified Connection's stable key
    /// (matched by Provider + OrganizationUrl from the AddConnectionsTable backfill).
    /// </summary>
    public partial class AddRepositoryProjectAndRepointConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add Project column (nullable initially for backfill).
            migrationBuilder.AddColumn<string>(
                name: "Project",
                table: "Repositories",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            // 2. Backfill Project from the previously-referenced RepositoryHostConnection.Project.
            // Match on RepositoryHostConnectionKey -> RepositoryHostConnections.Key.
            migrationBuilder.Sql(
                @"UPDATE Repositories
                  SET ""Project"" = (
                      SELECT rhc.""Project""
                      FROM ""RepositoryHostConnections"" rhc
                      WHERE rhc.""Key"" = Repositories.""RepositoryHostConnectionKey""
                      LIMIT 1
                  )
                  WHERE ""Project"" IS NULL
                    AND ""RepositoryHostConnectionKey"" IS NOT NULL
                    AND ""RepositoryHostConnectionKey"" != ''");

            // 3. Retarget RepositoryHostConnectionKey to the unified Connection key.
            // Match via RepositoryHostConnections -> (Provider mapped, OrganizationUrl) -> Connections.
            // Provider mapping: AzureDevOpsRepos -> AzureDevOps.
            migrationBuilder.Sql(
                @"UPDATE Repositories
                  SET ""RepositoryHostConnectionKey"" = (
                      SELECT c.""Key""
                      FROM ""Connections"" c
                      INNER JOIN ""RepositoryHostConnections"" rhc
                        ON c.""Provider"" = 'AzureDevOps'
                        AND c.""OrganizationUrl"" = rhc.""OrganizationUrl""
                      WHERE rhc.""Key"" = Repositories.""RepositoryHostConnectionKey""
                      LIMIT 1
                  )
                  WHERE ""RepositoryHostConnectionKey"" IS NOT NULL
                    AND ""RepositoryHostConnectionKey"" != ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: restore RepositoryHostConnectionKey to the legacy key
            // and drop the Project column.

            // 1. Restore RepositoryHostConnectionKey to the legacy RepositoryHostConnections key.
            // This is lossy if the legacy table was modified or deleted.
            migrationBuilder.Sql(
                @"UPDATE Repositories
                  SET ""RepositoryHostConnectionKey"" = (
                      SELECT rhc.""Key""
                      FROM ""RepositoryHostConnections"" rhc
                      INNER JOIN ""Connections"" c
                        ON c.""Key"" = Repositories.""RepositoryHostConnectionKey""
                        AND c.""Provider"" = 'AzureDevOps'
                        AND c.""OrganizationUrl"" = rhc.""OrganizationUrl""
                      LIMIT 1
                  )
                  WHERE ""RepositoryHostConnectionKey"" IS NOT NULL
                    AND ""RepositoryHostConnectionKey"" != ''");

            // 2. Drop Project column.
            migrationBuilder.DropColumn(
                name: "Project",
                table: "Repositories");
        }
    }
}
