using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Auto-generated EF Core migration — inline array arguments are intentional

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddReworkFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReworkFeedback",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OriginatingRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PullRequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FeedbackBundleId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FirstQualifyingCommentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastQualifyingCommentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ThreadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReworkFeedback", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReworkFeedback_PullRequestId_FeedbackBundleId",
                table: "ReworkFeedback",
                columns: new[] { "PullRequestId", "FeedbackBundleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReworkFeedback_Status",
                table: "ReworkFeedback",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReworkFeedback");
        }
    }
}
