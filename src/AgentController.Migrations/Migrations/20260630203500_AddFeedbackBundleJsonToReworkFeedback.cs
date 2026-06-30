using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Auto-generated EF Core migration — inline array arguments are intentional

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackBundleJsonToReworkFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeedbackBundleJson",
                table: "ReworkFeedback",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeedbackBundleJson",
                table: "ReworkFeedback");
        }
    }
}
