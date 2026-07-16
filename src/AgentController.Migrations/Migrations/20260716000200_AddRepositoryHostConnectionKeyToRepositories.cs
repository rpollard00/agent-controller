using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <summary>
    /// Adds RepositoryHostConnectionKey and RemoteIdentity columns to the Repositories table.
    /// Backfills RepositoryHostConnectionKey from AzureDevOpsEnvironmentKey where a matching
    /// host connection exists.
    /// </summary>
    public partial class AddRepositoryHostConnectionKeyToRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RepositoryHostConnectionKey",
                table: "Repositories",
                type: "TEXT",
                maxLength: 128,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "RemoteIdentity",
                table: "Repositories",
                type: "TEXT",
                maxLength: 256,
                nullable: true
            );

            // Backfill: set RepositoryHostConnectionKey from AzureDevOpsEnvironmentKey
            // where a matching RepositoryHostConnection exists.
            migrationBuilder.Sql(@"
                UPDATE Repositories
                SET RepositoryHostConnectionKey = AzureDevOpsEnvironmentKey
                WHERE AzureDevOpsEnvironmentKey IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM RepositoryHostConnections
                      WHERE RepositoryHostConnections.Key = Repositories.AzureDevOpsEnvironmentKey
                  )
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepositoryHostConnectionKey",
                table: "Repositories"
            );

            migrationBuilder.DropColumn(
                name: "RemoteIdentity",
                table: "Repositories"
            );
        }
    }
}
