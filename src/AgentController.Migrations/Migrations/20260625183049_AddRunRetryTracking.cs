using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <summary>
    /// Add run-level retry tracking fields to AgentRuns.
    /// - RunAttempt: which run attempt this is for the work item (1-based, default 1).
    /// - PreviousRunId: identifier of the previous run in the retry chain (null for initial runs).
    /// </summary>
    public partial class AddRunRetryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RunAttempt",
                table: "AgentRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "PreviousRunId",
                table: "AgentRuns",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousRunId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RunAttempt",
                table: "AgentRuns");
        }
    }
}
