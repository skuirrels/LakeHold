using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakehold.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryLanguages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "SavedQueries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "sql");

            migrationBuilder.AddColumn<string>(
                name: "PublishedSchemaFingerprint",
                table: "SavedQueries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "QueryRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "PublishedSchemaFingerprint",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "QueryRuns");
        }
    }
}
