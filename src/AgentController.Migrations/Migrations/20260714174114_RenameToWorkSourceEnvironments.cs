using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RenameToWorkSourceEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "AzureDevOpsEnvironments",
                newName: "WorkSourceEnvironments");

            migrationBuilder.AddColumn<string>(
                name: "CompletedStatesJson",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "AzureDevOpsBoards");

            migrationBuilder.AddColumn<string>(
                name: "TagPrefix",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "agent");

            migrationBuilder.DropColumn(
                name: "EligibleStatesJson",
                table: "WorkSourceEnvironments");

            migrationBuilder.DropColumn(
                name: "EligibleTagsJson",
                table: "WorkSourceEnvironments");

            migrationBuilder.DropColumn(
                name: "ExcludedStatesJson",
                table: "WorkSourceEnvironments");

            migrationBuilder.DropColumn(
                name: "ExcludedTagsJson",
                table: "WorkSourceEnvironments");

            migrationBuilder.DropColumn(
                name: "WorkItemType",
                table: "WorkSourceEnvironments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EligibleStatesJson",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "EligibleTagsJson",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ExcludedStatesJson",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ExcludedTagsJson",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "WorkItemType",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "User Story");

            migrationBuilder.DropColumn(
                name: "CompletedStatesJson",
                table: "WorkSourceEnvironments");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "WorkSourceEnvironments");

            migrationBuilder.DropColumn(
                name: "TagPrefix",
                table: "WorkSourceEnvironments");

            migrationBuilder.RenameTable(
                name: "WorkSourceEnvironments",
                newName: "AzureDevOpsEnvironments");
        }
    }
}
