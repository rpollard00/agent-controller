using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PersistRuntimeEnvironmentProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeEnvironments",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnvironmentProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkspaceRoot = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    RuntimeProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PiExecutablePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ControllerBaseUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    PtyWrapperPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    PtyWrapperArgs = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    LoadoutsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ForwardEnvironmentVariablesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeEnvironments", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeEnvironments");
        }
    }
}
