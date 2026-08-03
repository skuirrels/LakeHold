using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration index column arrays.

namespace Lakehold.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicApiFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiIdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Scope = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResponseContentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResponseLocation = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ResponseBody = table.Column<byte[]>(type: "bytea", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiIdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TenantSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CatalogName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedByTokenId = table.Column<int>(type: "integer", nullable: true),
                    ResultJson = table.Column<string>(type: "character varying(1048576)", maxLength: 1048576, nullable: true),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiIdempotencyRecords_Scope_KeyHash",
                table: "ApiIdempotencyRecords",
                columns: new[] { "Scope", "KeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiIdempotencyRecords_Status_CompletedUtc",
                table: "ApiIdempotencyRecords",
                columns: new[] { "Status", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiOperations_Status_CompletedUtc",
                table: "ApiOperations",
                columns: new[] { "Status", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiOperations_Status_CreatedUtc",
                table: "ApiOperations",
                columns: new[] { "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiOperations_TenantSlug_CreatedUtc",
                table: "ApiOperations",
                columns: new[] { "TenantSlug", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiIdempotencyRecords");

            migrationBuilder.DropTable(
                name: "ApiOperations");
        }
    }
}
