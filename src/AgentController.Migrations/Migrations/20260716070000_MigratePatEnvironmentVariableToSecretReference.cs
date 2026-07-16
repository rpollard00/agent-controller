using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Drops the legacy PatEnvironmentVariable column from WorkSourceEnvironments.
    /// Existing rows have their PatEnvironmentVariable value migrated into
    /// PersonalAccessTokenSecretName so that SecretReference resolution continues
    /// to work (operators must create corresponding named secrets).
    /// </summary>
    public partial class MigratePatEnvironmentVariableToSecretReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add SecretReference columns if they don't exist yet.
            // These were added to the entity model in a prior work item but may not
            // have been captured in a migration when using EnsureCreated().
            migrationBuilder.Sql(
                "ALTER TABLE WorkSourceEnvironments " +
                "ADD COLUMN PersonalAccessTokenSecretName TEXT DEFAULT '' CHECK(length(PersonalAccessTokenSecretName) <= 256)");
            migrationBuilder.Sql(
                "ALTER TABLE WorkSourceEnvironments " +
                "ADD COLUMN PersonalAccessTokenSecretVersion INTEGER");

            // Migrate existing PatEnvironmentVariable values into PersonalAccessTokenSecretName
            // for rows that don't already have a secret reference.
            migrationBuilder.Sql(
                "UPDATE WorkSourceEnvironments " +
                "SET PersonalAccessTokenSecretName = PatEnvironmentVariable " +
                "WHERE (PersonalAccessTokenSecretName IS NULL OR PersonalAccessTokenSecretName = '') " +
                "AND (PatEnvironmentVariable IS NOT NULL AND PatEnvironmentVariable != '')");

            // Drop the legacy column.
            migrationBuilder.DropColumn(
                name: "PatEnvironmentVariable",
                table: "WorkSourceEnvironments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PatEnvironmentVariable",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // Restore PatEnvironmentVariable from PersonalAccessTokenSecretName.
            migrationBuilder.Sql(
                "UPDATE WorkSourceEnvironments " +
                "SET PatEnvironmentVariable = PersonalAccessTokenSecretName " +
                "WHERE PersonalAccessTokenSecretName IS NOT NULL AND PersonalAccessTokenSecretName != ''");

            // Drop the SecretReference columns.
            migrationBuilder.DropColumn(
                name: "PersonalAccessTokenSecretName",
                table: "WorkSourceEnvironments");
            migrationBuilder.DropColumn(
                name: "PersonalAccessTokenSecretVersion",
                table: "WorkSourceEnvironments");
        }
    }
}
