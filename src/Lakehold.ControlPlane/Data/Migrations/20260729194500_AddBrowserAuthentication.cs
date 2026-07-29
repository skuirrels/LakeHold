using Lakehold.ControlPlane.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lakehold.ControlPlane.Data.Migrations;

/// <summary>Adds the shared key ring used to protect browser authentication cookies across API nodes.</summary>
[DbContext(typeof(ControlPlaneContext))]
[Migration("20260729194500_AddBrowserAuthentication")]
public partial class AddBrowserAuthentication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DataProtectionKeys",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation(
                        "Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                FriendlyName = table.Column<string>(type: "text", nullable: true),
                Xml = table.Column<string>(type: "text", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_DataProtectionKeys", row => row.Id));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "DataProtectionKeys");
}
