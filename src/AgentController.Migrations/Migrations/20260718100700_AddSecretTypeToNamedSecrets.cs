using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the SecretType column to NamedSecrets with a default value of
    /// "personal-access-token" to migrate any existing unnamed (legacy) secrets.
    /// New code uses explicit typed creation so every row has a meaningful type.
    /// </summary>
    public partial class AddSecretTypeToNamedSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecretType",
                table: "NamedSecrets",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "personal-access-token");

            // Ensure any existing rows that were created before this migration
            // receive the PAT type.
            migrationBuilder.Sql(
                "UPDATE \"NamedSecrets\" SET \"SecretType\" = 'personal-access-token' WHERE \"SecretType\" IS NULL OR \"SecretType\" = ''");

            migrationBuilder.CreateIndex(
                name: "IX_NamedSecrets_SecretType",
                table: "NamedSecrets",
                column: "SecretType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NamedSecrets_SecretType",
                table: "NamedSecrets");

            migrationBuilder.DropColumn(
                name: "SecretType",
                table: "NamedSecrets");
        }
    }
}
