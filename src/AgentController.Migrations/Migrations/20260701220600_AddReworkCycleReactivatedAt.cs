using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Auto-generated EF Core migration — inline array arguments are intentional

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddReworkCycleReactivatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset?>(
                name: "ReactivatedAt",
                table: "ReworkCycles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReactivatedAt",
                table: "ReworkCycles");
        }
    }
}
