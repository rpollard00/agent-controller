using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Auto-generated EF Core migration — inline array arguments are intentional

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddReworkCycles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReworkCycles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CycleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    PriorRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PullRequestUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    BaseCommitSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FeedbackBundleJson = table.Column<string>(type: "TEXT", nullable: false),
                    FeedbackBundleId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    NewRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReworkCycles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReworkCycles_FeedbackBundleId",
                table: "ReworkCycles",
                column: "FeedbackBundleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReworkCycles_WorkItemId_Status",
                table: "ReworkCycles",
                columns: new[] { "WorkItemId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReworkCycles");
        }
    }
}
