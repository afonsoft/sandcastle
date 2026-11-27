using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec163_hook_deliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automation_hook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_hook_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_hook_deliveries_hook_id_idempotency_key",
                table: "automation_hook_deliveries",
                columns: new[] { "hook_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_hook_deliveries_received_at_ticks",
                table: "automation_hook_deliveries",
                column: "received_at_ticks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_hook_deliveries");
        }
    }
}