using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations
{
    /// <summary>
    /// Rework WorkSourceEnvironments table to reference unified Connections.
    /// Drops inline OrganizationUrl, Project, PersonalAccessTokenSecretName/Version;
    /// adds ConnectionKey; backfills ConnectionKey from Connections table.
    /// </summary>
    public partial class ReworkWorkSourceEnvironmentToConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add ConnectionKey column (nullable initially for backfill).
            migrationBuilder.AddColumn<string>(
                name: "ConnectionKey",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                maxLength: 128,
                nullable: true,
                defaultValue: "");

            // 2. Backfill ConnectionKey from the Connections table.
            // Match on (normalized Provider, OrganizationUrl extracted from JSON settings).
            // Provider mapping: AzureDevOpsBoards -> AzureDevOps.
            // Connection key derivation: azuredevops-{org-name-from-url}.
            migrationBuilder.Sql(
                @"UPDATE WorkSourceEnvironments
                  SET ConnectionKey = (
                      SELECT c.""Key""
                      FROM ""Connections"" c
                      WHERE c.""Provider"" = 'AzureDevOps'
                        AND json_extract(c.""ProviderSettingsJson"", '$.OrganizationUrl') = WorkSourceEnvironments.""OrganizationUrl""
                      LIMIT 1
                  )
                  WHERE ConnectionKey IS NULL
                    AND ""Provider"" IN ('AzureDevOpsBoards', 'AzureDevOps')");

            // 3. Make ConnectionKey NOT NULL (all rows should have a value after backfill).
            migrationBuilder.Sql(
                @"UPDATE WorkSourceEnvironments
                  SET ConnectionKey = ''
                  WHERE ConnectionKey IS NULL");

            migrationBuilder.AlterColumn<string>(
                name: "ConnectionKey",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128,
                oldNullable: true);

            // 4. Drop columns moved to the connection.
            migrationBuilder.DropColumn(
                name: "OrganizationUrl",
                table: "WorkSourceEnvironments");

            migrationBuilder.DropColumn(
                name: "PersonalAccessTokenSecretName",
                table: "WorkSourceEnvironments");

            migrationBuilder.DropColumn(
                name: "PersonalAccessTokenSecretVersion",
                table: "WorkSourceEnvironments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore columns from the connection's ProviderSettingsJson.
            // Note: this is lossy if the connection was deleted or modified.

            // 1. Add back the dropped columns.
            migrationBuilder.AddColumn<string>(
                name: "OrganizationUrl",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PersonalAccessTokenSecretName",
                table: "WorkSourceEnvironments",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PersonalAccessTokenSecretVersion",
                table: "WorkSourceEnvironments",
                type: "INTEGER",
                nullable: true);

            // 2. Backfill OrganizationUrl and PAT from the connection's JSON settings.
            migrationBuilder.Sql(
                @"UPDATE WorkSourceEnvironments
                  SET ""OrganizationUrl"" = (
                      SELECT json_extract(c.""ProviderSettingsJson"", '$.organizationUrl')
                      FROM ""Connections"" c
                      WHERE c.""Key"" = WorkSourceEnvironments.""ConnectionKey""
                      LIMIT 1
                  ),
                  ""PersonalAccessTokenSecretName"" = (
                      SELECT json_extract(
                          json_extract(c.""ProviderSettingsJson"", '$.personalAccessTokenReference'),
                          '$.name'
                      )
                      FROM ""Connections"" c
                      WHERE c.""Key"" = WorkSourceEnvironments.""ConnectionKey""
                      LIMIT 1
                  ),
                  ""PersonalAccessTokenSecretVersion"" = (
                      SELECT json_extract(
                          json_extract(c.""ProviderSettingsJson"", '$.personalAccessTokenReference'),
                          '$.version'
                      )
                      FROM ""Connections"" c
                      WHERE c.""Key"" = WorkSourceEnvironments.""ConnectionKey""
                      LIMIT 1
                  )
                  WHERE ""OrganizationUrl"" = ''");

            // 3. Drop ConnectionKey.
            migrationBuilder.DropColumn(
                name: "ConnectionKey",
                table: "WorkSourceEnvironments");
        }
    }
}
