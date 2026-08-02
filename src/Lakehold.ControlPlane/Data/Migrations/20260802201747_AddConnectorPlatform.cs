using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakehold.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdapterId",
                table: "DataConnectors",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "lakehold.rest");

            migrationBuilder.AddColumn<int>(
                name: "AdapterVersion",
                table: "DataConnectors",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "AuthenticationJson",
                table: "DataConnectors",
                type: "character varying(16384)",
                maxLength: 16384,
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Checkpoint",
                table: "DataConnectors",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CheckpointVersion",
                table: "DataConnectors",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "DataConnectors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FieldMappingsJson",
                table: "DataConnectors",
                type: "character varying(65536)",
                maxLength: 65536,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "KeyColumnsJson",
                table: "DataConnectors",
                type: "character varying(16384)",
                maxLength: 16384,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                table: "DataConnectors",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PausedUtc",
                table: "DataConnectors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReadMode",
                table: "DataConnectors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RetryBaseSeconds",
                table: "DataConnectors",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "RetryMaxSeconds",
                table: "DataConnectors",
                type: "integer",
                nullable: false,
                defaultValue: 3600);

            migrationBuilder.AddColumn<int>(
                name: "SchemaPolicy",
                table: "DataConnectors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceSettingsJson",
                table: "DataConnectors",
                type: "character varying(32768)",
                maxLength: 32768,
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "InputCheckpoint",
                table: "DataConnectorRuns",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE \"DataConnectors\" SET \"AdapterId\" = CASE \"Kind\" "
                + "WHEN 0 THEN 'lakehold.rest' WHEN 1 THEN 'lakehold.grpc' ELSE \"AdapterId\" END");
            migrationBuilder.Sql(
                "UPDATE \"DataConnectors\" SET \"AuthenticationJson\" = "
                + "json_build_object('Kind', 1, 'SecretReference', "
                + "'env://' || \"CredentialEnvironmentVariable\")::text "
                + "WHERE \"CredentialEnvironmentVariable\" IS NOT NULL");

            migrationBuilder.AddColumn<string>(
                name: "ProposedCheckpoint",
                table: "DataConnectorRuns",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplayKey",
                table: "DataConnectorRuns",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdapterId",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "AdapterVersion",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "AuthenticationJson",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "Checkpoint",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "CheckpointVersion",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "FieldMappingsJson",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "KeyColumnsJson",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "PausedUtc",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "ReadMode",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "RetryBaseSeconds",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "RetryMaxSeconds",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "SchemaPolicy",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "SourceSettingsJson",
                table: "DataConnectors");

            migrationBuilder.DropColumn(
                name: "InputCheckpoint",
                table: "DataConnectorRuns");

            migrationBuilder.DropColumn(
                name: "ProposedCheckpoint",
                table: "DataConnectorRuns");

            migrationBuilder.DropColumn(
                name: "ReplayKey",
                table: "DataConnectorRuns");
        }
    }
}
