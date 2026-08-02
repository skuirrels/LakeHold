using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable
#pragma warning disable CA1861 // EF-generated composite index column arrays.

namespace Lakehold.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedDataConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataConnectors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    CatalogId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TagsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    EndpointUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CredentialEnvironmentVariable = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RestResponseFormat = table.Column<int>(type: "integer", nullable: false),
                    TargetSchema = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    TargetTable = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    MinimumRows = table.Column<long>(type: "bigint", nullable: false),
                    RequiredColumnsJson = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    NotNullColumnsJson = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RefreshIntervalSeconds = table.Column<int>(type: "integer", nullable: true),
                    NextRunUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TargetProvisioned = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataConnectors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataConnectors_Catalogs_CatalogId",
                        column: x => x.CatalogId,
                        principalTable: "Catalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DataConnectors_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataConnectorRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DataConnectorId = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LeaseToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowsRead = table.Column<long>(type: "bigint", nullable: false),
                    RowsPublished = table.Column<long>(type: "bigint", nullable: false),
                    QualityPassed = table.Column<bool>(type: "boolean", nullable: true),
                    SourceVersion = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataConnectorRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataConnectorRuns_DataConnectors_DataConnectorId",
                        column: x => x.DataConnectorId,
                        principalTable: "DataConnectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataConnectorRuns_DataConnectorId_StartedUtc",
                table: "DataConnectorRuns",
                columns: new[] { "DataConnectorId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DataConnectors_CatalogId_Name",
                table: "DataConnectors",
                columns: new[] { "CatalogId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataConnectors_CatalogId_TargetSchema_TargetTable",
                table: "DataConnectors",
                columns: new[] { "CatalogId", "TargetSchema", "TargetTable" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataConnectors_Enabled_NextRunUtc_LeaseExpiresUtc",
                table: "DataConnectors",
                columns: new[] { "Enabled", "NextRunUtc", "LeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DataConnectors_TenantId",
                table: "DataConnectors",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataConnectorRuns");

            migrationBuilder.DropTable(
                name: "DataConnectors");
        }
    }
}
