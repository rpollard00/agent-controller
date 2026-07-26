using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Auto-generated EF Core migration — inline array arguments are intentional

namespace AgentController.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class PersistAssistanceMaterializationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReworkFeedback_PullRequestId_FeedbackBundleId",
                table: "ReworkFeedback");

            migrationBuilder.DropIndex(
                name: "IX_ReworkCycles_FeedbackBundleId",
                table: "ReworkCycles");

            migrationBuilder.AlterColumn<string>(
                name: "OriginatingRunId",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<string>(
                name: "AssistanceStoryExternalId",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistanceStoryUrl",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistanceStoryWorkItemId",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CanonicalPullRequestKey",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestEnvironmentKey",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestRepositoryKey",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestSourceBranch",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestSourceCommitSha",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestTargetBranch",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestUrl",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestMode",
                table: "ReworkFeedback",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "PriorRunId",
                table: "ReworkCycles",
                type: "TEXT",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<string>(
                name: "CanonicalPullRequestKey",
                table: "ReworkCycles",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "ReworkCycles",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestEnvironmentKey",
                table: "ReworkCycles",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestId",
                table: "ReworkCycles",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestRepositoryKey",
                table: "ReworkCycles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestTargetBranch",
                table: "ReworkCycles",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestMode",
                table: "ReworkCycles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ReworkFeedback_CorrelationId",
                table: "ReworkFeedback",
                column: "CorrelationId",
                unique: true,
                filter: "\"CorrelationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReworkFeedback_LegacyPullRequest_FeedbackBundleId",
                table: "ReworkFeedback",
                columns: new[] { "RequestMode", "PullRequestId", "FeedbackBundleId" },
                unique: true,
                filter: "\"CanonicalPullRequestKey\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReworkFeedback_RequestMode_PrKey_FeedbackBundleId",
                table: "ReworkFeedback",
                columns: new[] { "RequestMode", "CanonicalPullRequestKey", "FeedbackBundleId" },
                unique: true,
                filter: "\"CanonicalPullRequestKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReworkFeedback_RequestMode_PrKey_Status",
                table: "ReworkFeedback",
                columns: new[] { "RequestMode", "CanonicalPullRequestKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReworkCycles_CorrelationId",
                table: "ReworkCycles",
                column: "CorrelationId",
                unique: true,
                filter: "\"CorrelationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReworkCycles_FeedbackBundleId",
                table: "ReworkCycles",
                column: "FeedbackBundleId",
                unique: true,
                filter: "\"CanonicalPullRequestKey\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReworkCycles_RequestMode_PrKey_CycleNumber",
                table: "ReworkCycles",
                columns: new[] { "RequestMode", "CanonicalPullRequestKey", "CycleNumber" },
                unique: true,
                filter: "\"CanonicalPullRequestKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReworkCycles_RequestMode_PrKey_FeedbackBundleId",
                table: "ReworkCycles",
                columns: new[] { "RequestMode", "CanonicalPullRequestKey", "FeedbackBundleId" },
                unique: true,
                filter: "\"CanonicalPullRequestKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReworkCycles_RequestMode_PrKey_Status",
                table: "ReworkCycles",
                columns: new[] { "RequestMode", "CanonicalPullRequestKey", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReworkFeedback_CorrelationId",
                table: "ReworkFeedback");

            migrationBuilder.DropIndex(
                name: "IX_ReworkFeedback_LegacyPullRequest_FeedbackBundleId",
                table: "ReworkFeedback");

            migrationBuilder.DropIndex(
                name: "IX_ReworkFeedback_RequestMode_PrKey_FeedbackBundleId",
                table: "ReworkFeedback");

            migrationBuilder.DropIndex(
                name: "IX_ReworkFeedback_RequestMode_PrKey_Status",
                table: "ReworkFeedback");

            migrationBuilder.DropIndex(
                name: "IX_ReworkCycles_CorrelationId",
                table: "ReworkCycles");

            migrationBuilder.DropIndex(
                name: "IX_ReworkCycles_FeedbackBundleId",
                table: "ReworkCycles");

            migrationBuilder.DropIndex(
                name: "IX_ReworkCycles_RequestMode_PrKey_CycleNumber",
                table: "ReworkCycles");

            migrationBuilder.DropIndex(
                name: "IX_ReworkCycles_RequestMode_PrKey_FeedbackBundleId",
                table: "ReworkCycles");

            migrationBuilder.DropIndex(
                name: "IX_ReworkCycles_RequestMode_PrKey_Status",
                table: "ReworkCycles");

            migrationBuilder.DropColumn(
                name: "AssistanceStoryExternalId",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "AssistanceStoryUrl",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "AssistanceStoryWorkItemId",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "CanonicalPullRequestKey",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "PullRequestEnvironmentKey",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "PullRequestRepositoryKey",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "PullRequestSourceBranch",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "PullRequestSourceCommitSha",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "PullRequestTargetBranch",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "PullRequestUrl",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "RequestMode",
                table: "ReworkFeedback");

            migrationBuilder.DropColumn(
                name: "CanonicalPullRequestKey",
                table: "ReworkCycles");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "ReworkCycles");

            migrationBuilder.DropColumn(
                name: "PullRequestEnvironmentKey",
                table: "ReworkCycles");

            migrationBuilder.DropColumn(
                name: "PullRequestId",
                table: "ReworkCycles");

            migrationBuilder.DropColumn(
                name: "PullRequestRepositoryKey",
                table: "ReworkCycles");

            migrationBuilder.DropColumn(
                name: "PullRequestTargetBranch",
                table: "ReworkCycles");

            migrationBuilder.DropColumn(
                name: "RequestMode",
                table: "ReworkCycles");

            migrationBuilder.Sql(
                "UPDATE ReworkFeedback SET OriginatingRunId = '' WHERE OriginatingRunId IS NULL;");
            migrationBuilder.Sql(
                "UPDATE ReworkCycles SET PriorRunId = '' WHERE PriorRunId IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "OriginatingRunId",
                table: "ReworkFeedback",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PriorRunId",
                table: "ReworkCycles",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReworkFeedback_PullRequestId_FeedbackBundleId",
                table: "ReworkFeedback",
                columns: new[] { "PullRequestId", "FeedbackBundleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReworkCycles_FeedbackBundleId",
                table: "ReworkCycles",
                column: "FeedbackBundleId",
                unique: true);
        }
    }
}
