using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <summary>
    /// Adds Transport column (CloneTransport enum stored as INTEGER) to the
    /// Repositories table. Defaults to 0 (Unspecified) so existing rows
    /// backfill cleanly on SQLite.
    /// </summary>
    public partial class AddRepositoryTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Transport",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Transport",
                table: "Repositories");
        }
    }
}
