using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lakehold.ControlPlane.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableCdcDeliveries : Migration
    {
        private static readonly string[] DeliveryScheduleColumns =
            ["DeliveredUtc", "NextAttemptUtc", "LeaseExpiresUtc"];
        private static readonly string[] SubscriptionSnapshotColumns =
            ["SubscriptionId", "SnapshotId"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangeDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubscriptionId = table.Column<int>(type: "integer", nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: true),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeDeliveries_ChangeSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "ChangeSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeDeliveries_DeliveredUtc_NextAttemptUtc_LeaseExpiresUtc",
                table: "ChangeDeliveries",
                columns: DeliveryScheduleColumns);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeDeliveries_DeliveryId",
                table: "ChangeDeliveries",
                column: "DeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeDeliveries_SubscriptionId_SnapshotId",
                table: "ChangeDeliveries",
                columns: SubscriptionSnapshotColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeDeliveries");
        }
    }
}
