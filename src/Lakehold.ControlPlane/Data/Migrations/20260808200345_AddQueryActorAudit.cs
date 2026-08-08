using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakehold.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryActorAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorKind",
                table: "QueryRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<int>(
                name: "MemberId",
                table: "QueryRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "QueryRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActorKind",
                table: "QueryRuns");

            migrationBuilder.DropColumn(
                name: "MemberId",
                table: "QueryRuns");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "QueryRuns");
        }
    }
}
