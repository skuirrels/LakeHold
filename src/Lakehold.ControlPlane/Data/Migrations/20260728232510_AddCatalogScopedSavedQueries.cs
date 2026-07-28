using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakehold.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogScopedSavedQueries : Migration
    {
        private static readonly string[] CatalogNameColumns = ["CatalogId", "Name"];
        private static readonly string[] TenantNameColumns = ["TenantId", "Name"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedQueries_TenantId_Name",
                table: "SavedQueries");

            migrationBuilder.AddColumn<int>(
                name: "CatalogId",
                table: "SavedQueries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcurrencyVersion",
                table: "SavedQueries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByTokenId",
                table: "SavedQueries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublishedRevision",
                table: "SavedQueries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedSchema",
                table: "SavedQueries",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedUtc",
                table: "SavedQueries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedViewName",
                table: "SavedQueries",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "SavedQueries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByTokenId",
                table: "SavedQueries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueries_CatalogId_Name",
                table: "SavedQueries",
                columns: CatalogNameColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueries_TenantId",
                table: "SavedQueries",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_SavedQueries_Catalogs_CatalogId",
                table: "SavedQueries",
                column: "CatalogId",
                principalTable: "Catalogs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SavedQueries_Catalogs_CatalogId",
                table: "SavedQueries");

            migrationBuilder.DropIndex(
                name: "IX_SavedQueries_CatalogId_Name",
                table: "SavedQueries");

            migrationBuilder.DropIndex(
                name: "IX_SavedQueries_TenantId",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "CatalogId",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "ConcurrencyVersion",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "CreatedByTokenId",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "PublishedRevision",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "PublishedSchema",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "PublishedUtc",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "PublishedViewName",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "UpdatedByTokenId",
                table: "SavedQueries");

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueries_TenantId_Name",
                table: "SavedQueries",
                columns: TenantNameColumns,
                unique: true);
        }
    }
}
