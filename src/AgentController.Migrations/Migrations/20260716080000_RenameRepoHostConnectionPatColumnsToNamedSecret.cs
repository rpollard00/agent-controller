using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <summary>
    /// Replaces PersonalAccessTokenReferenceKind + PersonalAccessTokenReferenceId
    /// with PersonalAccessTokenSecretName on the RepositoryHostConnections table.
    /// Migrates existing rows: the Id value becomes the secret name.
    /// </summary>
    public partial class RenameRepoHostConnectionPatColumnsToNamedSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the new column first (allow null temporarily).
            migrationBuilder.AddColumn<string>(
                name: "PersonalAccessTokenSecretName",
                table: "RepositoryHostConnections",
                type: "TEXT",
                maxLength: 256,
                nullable: true,
                defaultValue: "");

            // Migrate data: use the existing Id as the new secret name.
            migrationBuilder.Sql(
                "UPDATE RepositoryHostConnections " +
                "SET PersonalAccessTokenSecretName = PersonalAccessTokenReferenceId " +
                "WHERE PersonalAccessTokenSecretName IS NULL OR PersonalAccessTokenSecretName = ''"
            );

            // Make the new column non-nullable.
            migrationBuilder.AlterColumn<string>(
                name: "PersonalAccessTokenSecretName",
                table: "RepositoryHostConnections",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // Drop the old columns.
            migrationBuilder.DropColumn(
                name: "PersonalAccessTokenReferenceId",
                table: "RepositoryHostConnections");

            migrationBuilder.DropColumn(
                name: "PersonalAccessTokenReferenceKind",
                table: "RepositoryHostConnections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-add the old columns.
            migrationBuilder.AddColumn<string>(
                name: "PersonalAccessTokenReferenceKind",
                table: "RepositoryHostConnections",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "Db");

            migrationBuilder.AddColumn<string>(
                name: "PersonalAccessTokenReferenceId",
                table: "RepositoryHostConnections",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // Migrate data back: use the secret name as the Id, default Kind to "Db".
            migrationBuilder.Sql(
                "UPDATE RepositoryHostConnections " +
                "SET PersonalAccessTokenReferenceId = PersonalAccessTokenSecretName " +
                "WHERE PersonalAccessTokenReferenceId = ''"
            );

            // Drop the new column.
            migrationBuilder.DropColumn(
                name: "PersonalAccessTokenSecretName",
                table: "RepositoryHostConnections");
        }
    }
}
