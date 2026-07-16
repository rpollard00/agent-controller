using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the RepositoryHostConnections table for managed repository host connection profiles.
    /// </summary>
    public partial class AddRepositoryHostConnectionsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepositoryHostConnections",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OrganizationUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Project = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PersonalAccessTokenReferenceKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PersonalAccessTokenReferenceId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryHostConnections", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RepositoryHostConnections");
        }
    }
}
