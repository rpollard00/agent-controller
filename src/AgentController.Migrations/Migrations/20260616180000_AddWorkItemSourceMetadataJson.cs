using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <summary>
    /// Adds SourceMetadataJson column to WorkItems table so externally
    /// discovered work candidates can persist opaque source metadata
    /// (Azure DevOps revision, area path, iteration path, work item type)
    /// for later status projection with optimistic concurrency.
    /// </summary>
    public partial class AddWorkItemSourceMetadataJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceMetadataJson",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceMetadataJson",
                table: "WorkItems");
        }
    }
}
