using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAzureDevOpsEnvironmentKeyFromRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AzureDevOpsEnvironmentKey",
                table: "Repositories");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AzureDevOpsEnvironmentKey",
                table: "Repositories",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }
    }
}
