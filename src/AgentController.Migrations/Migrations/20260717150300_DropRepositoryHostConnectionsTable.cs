using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations
{
    /// <summary>
    /// Drop the legacy RepositoryHostConnections table after all references
    /// have been repointed to the unified Connections table.
    /// 
    /// This migration depends on:
    /// - 20260717150000_AddConnectionsTable (created the Connections table)
    /// - 20260717150100_ReworkWorkSourceEnvironmentToConnection (repointed work sources)
    /// - 20260717150200_AddRepositoryProjectAndRepointConnection (repointed repositories)
    /// </summary>
    public partial class DropRepositoryHostConnectionsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // All references have been repointed to the unified Connections table.
            // Drop the legacy RepositoryHostConnections table.
            migrationBuilder.DropTable(name: "RepositoryHostConnections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreate the RepositoryHostConnections table for rollback.
            // Note: Data cannot be fully restored since it was deduplicated
            // into the Connections table during the AddConnectionsTable migration.
            migrationBuilder.CreateTable(
                name: "RepositoryHostConnections",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OrganizationUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Project = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PersonalAccessTokenSecretName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PersonalAccessTokenSecretVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryHostConnections", x => x.Key);
                });
        }
    }
}
