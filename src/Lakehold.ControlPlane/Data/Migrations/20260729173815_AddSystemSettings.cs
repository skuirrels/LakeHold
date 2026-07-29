using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakehold.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    McpEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    McpAllowWrites = table.Column<bool>(type: "boolean", nullable: false),
                    McpMaxRowsPerResult = table.Column<int>(type: "integer", nullable: false),
                    McpPublicBaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ConcurrencyVersion = table.Column<int>(type: "integer", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemSettings");
        }
    }
}
