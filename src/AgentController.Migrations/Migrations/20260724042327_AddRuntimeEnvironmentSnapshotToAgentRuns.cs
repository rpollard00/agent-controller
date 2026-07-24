using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeEnvironmentSnapshotToAgentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvironmentProviderType",
                table: "AgentRuns",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuntimeProfileName",
                table: "AgentRuns",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvironmentProviderType",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RuntimeProfileName",
                table: "AgentRuns");
        }
    }
}
