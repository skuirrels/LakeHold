using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakehold.ControlPlane.Data.Migrations;

public partial class AddConnectorSourceAcknowledgementRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "SourceAcknowledgementPendingUtc",
            table: "DataConnectors",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceAcknowledgementError",
            table: "DataConnectors",
            type: "character varying(4000)",
            maxLength: 4000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SourceAcknowledgementPendingUtc", table: "DataConnectors");
        migrationBuilder.DropColumn(name: "SourceAcknowledgementError", table: "DataConnectors");
    }
}
