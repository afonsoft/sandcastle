using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec167_model_stats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_stats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    model = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    runs = table.Column<long>(type: "bigint", nullable: false),
                    successes = table.Column<long>(type: "bigint", nullable: false),
                    failures = table.Column<long>(type: "bigint", nullable: false),
                    quality_ema = table.Column<double>(type: "double precision", nullable: false),
                    latency_ema_ms = table.Column<double>(type: "double precision", nullable: true),
                    total_cost_usd = table.Column<decimal>(type: "numeric", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_stats", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_stats_role_provider_model",
                table: "model_stats",
                columns: new[] { "role", "provider", "model" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_stats");
        }
    }
}
