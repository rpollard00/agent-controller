using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PersistAzureDevOpsEnvironmentProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AzureDevOpsEnvironments",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    OrganizationUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Project = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkItemType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EligibleTagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludedTagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EligibleStatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludedStatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActiveState = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CompletedState = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PatEnvironmentVariable = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AzureDevOpsEnvironments", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AzureDevOpsEnvironments");
        }
    }
}
