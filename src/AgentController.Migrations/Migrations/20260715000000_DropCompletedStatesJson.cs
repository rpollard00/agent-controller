using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class DropCompletedStatesJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedStatesJson",
                table: "WorkSourceEnvironments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompletedStatesJson",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
