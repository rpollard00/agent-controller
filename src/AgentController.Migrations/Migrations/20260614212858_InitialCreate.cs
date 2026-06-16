using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Auto-generated EF Core migration — inline array arguments are intentional

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    WorkerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RuntimeType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RuntimeRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EnvironmentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    PullRequestUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Environments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DestroyedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Environments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CloneUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EnvironmentProfile = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RuntimeProfile = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AllowedPathsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "WorkItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExternalSource = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExternalUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    RepoKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    AcceptanceCriteriaJson = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AssignedTo = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_EnvironmentId",
                table: "AgentRuns",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_Status",
                table: "AgentRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_Status_LastHeartbeatAt",
                table: "AgentRuns",
                columns: new[] { "Status", "LastHeartbeatAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_WorkItemId",
                table: "AgentRuns",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Environments_RunId",
                table: "Environments",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Environments_Status",
                table: "Environments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleEvents_RunId_CreatedAt",
                table: "LifecycleEvents",
                columns: new[] { "RunId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleEvents_RunId_EventId_Unique",
                table: "LifecycleEvents",
                columns: new[] { "RunId", "EventId" },
                unique: true,
                filter: "[EventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_RepoKey",
                table: "WorkItems",
                column: "RepoKey");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Source",
                table: "WorkItems",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Status_LeaseExpiresAt",
                table: "WorkItems",
                columns: new[] { "Status", "LeaseExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentRuns");

            migrationBuilder.DropTable(
                name: "Environments");

            migrationBuilder.DropTable(
                name: "LifecycleEvents");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropTable(
                name: "WorkItems");
        }
    }
}
