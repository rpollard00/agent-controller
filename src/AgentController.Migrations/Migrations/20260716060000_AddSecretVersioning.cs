using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Prefer 'static readonly' over constant array arguments (migration scaffolding)

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds versioned secret schema: NamedSecrets table with unique name index,
    /// and SecretVersions table with FK from versions to named secrets.
    /// SecretVersions stores encrypted value blobs, nonces, and wrapped DEKs —
    /// never plaintext values at rest.
    /// </summary>
    public partial class AddSecretVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NamedSecrets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NamedSecrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecretVersions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NamedSecretId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EncryptedValue = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    WrappedDek = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecretVersions_NamedSecrets_NamedSecretId",
                        column: x => x.NamedSecretId,
                        principalTable: "NamedSecrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NamedSecrets_Name",
                table: "NamedSecrets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecretVersions_NamedSecretId_VersionNumber",
                table: "SecretVersions",
                columns: new[] { "NamedSecretId", "VersionNumber" },
                unique: true);
        }
#pragma warning restore CA1861

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SecretVersions");
            migrationBuilder.DropTable(name: "NamedSecrets");
        }
    }
}
