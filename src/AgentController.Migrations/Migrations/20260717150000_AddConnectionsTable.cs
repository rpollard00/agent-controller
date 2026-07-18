using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentController.Migrations.Migrations
{
    /// <summary>
    /// Creates the Connections table and backfills from existing RepositoryHostConnections
    /// and WorkSourceEnvironments rows, deduped by (Provider, OrganizationUrl).
    /// A shared org becomes one connection carrying both capabilities.
    /// </summary>
    public partial class AddConnectionsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Connections",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: "AzureDevOps"),
                    Capabilities = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ProviderSettingsJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false, defaultValueSql: "STRFTIME('%Y-%m-%dT%H:%M:%fZ', 'NOW')"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false, defaultValueSql: "STRFTIME('%Y-%m-%dT%H:%M:%fZ', 'NOW')"),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connections", x => x.Key);
                });

            // Backfill Connections from RepositoryHostConnections (capability=1/Repositories)
            // and WorkSourceEnvironments (capability=2/WorkTracking), deduped by (Provider, OrganizationUrl).
            // Provider mapping: AzureDevOpsRepos/AzureDevOpsBoards → AzureDevOps.
            // Stable key derived as: lower(provider) + "-" + org-name-from-url.
            // Capabilities unioned via SUM.
            // ProviderSettingsJson carries OrganizationUrl + PersonalAccessTokenReference.Name.
            //
            // Note: Uses SQLite-compatible SQL (no PostgreSQL split_part).
            // Extract org name: substring after 3rd '/' and before '?' or end.
            migrationBuilder.Sql(
                @"INSERT OR IGNORE INTO Connections (Key, DisplayName, Enabled, Provider, Capabilities, ProviderSettingsJson, CreatedAt, UpdatedAt)
                SELECT
                    -- Derive stable key: lower(provider) + '-' + org name from URL
                    lower(
                        CASE
                            WHEN normalized_provider = 'AzureDevOpsRepos' THEN 'azuredevops'
                            WHEN normalized_provider = 'AzureDevOpsBoards' THEN 'azuredevops'
                            ELSE lower(normalized_provider)
                        END
                    ) || '-' ||
                    -- Extract org name from URL using SQLite substr/instr
                    replace(
                        case
                            when instr(substr(organization_url, instr(organization_url, '///') + 3), '?') > 0
                            then substr(organization_url, instr(organization_url, '///') + 3,
                                instr(substr(organization_url, instr(organization_url, '///') + 3), '?') - 1)
                            else substr(organization_url, instr(organization_url, '///') + 3)
                        end,
                        ' ', '-'
                    ) AS derived_key,
                    -- DisplayName: use the most common display name or derived name
                    max(display_name) AS display_name,
                    -- Enabled: true if any source is enabled
                    max(enabled) AS enabled,
                    -- Provider: normalized (AzureDevOpsRepos/AzureDevOpsBoards → AzureDevOps)
                    CASE
                        WHEN normalized_provider IN ('AzureDevOpsRepos', 'AzureDevOpsBoards') THEN 'AzureDevOps'
                        ELSE normalized_provider
                    END AS provider,
                    -- Capabilities: union via SUM (Repositories=1, WorkTracking=2)
                    sum(capability) AS capabilities,
                    -- ProviderSettingsJson: JSON with OrganizationUrl + PersonalAccessTokenReference
                    json_object(
                        'OrganizationUrl', organization_url,
                        'PersonalAccessTokenReference', json_object(
                            'Name', coalesce(max(personal_access_token_secret_name), '')
                        )
                    ) AS provider_settings_json,
                    -- Timestamps
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now') AS created_at,
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now') AS updated_at
                FROM (
                    -- RepositoryHostConnections → Repositories capability (1)
                    SELECT
                        Provider AS normalized_provider,
                        OrganizationUrl AS organization_url,
                        DisplayName AS display_name,
                        Enabled AS enabled,
                        1 AS capability,
                        PersonalAccessTokenSecretName AS personal_access_token_secret_name
                    FROM RepositoryHostConnections
                    UNION ALL
                    -- WorkSourceEnvironments → WorkTracking capability (2)
                    SELECT
                        Provider AS normalized_provider,
                        OrganizationUrl AS organization_url,
                        DisplayName AS display_name,
                        Enabled AS enabled,
                        2 AS capability,
                        PersonalAccessTokenSecretName AS personal_access_token_secret_name
                    FROM WorkSourceEnvironments
                )
                GROUP BY
                    CASE
                        WHEN normalized_provider IN ('AzureDevOpsRepos', 'AzureDevOpsBoards') THEN 'AzureDevOps'
                        ELSE normalized_provider
                    END,
                    organization_url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Connections");
        }
    }
}
