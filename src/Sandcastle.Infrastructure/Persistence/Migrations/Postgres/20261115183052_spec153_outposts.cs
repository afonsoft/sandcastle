using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec153_outposts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "outpost_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outposts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    labels_json = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    max_sandboxes = table.Column<int>(type: "integer", nullable: false),
                    last_heartbeat = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    enroll_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    enroll_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cert_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outposts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outposts_channel_key_hash",
                table: "outposts",
                column: "channel_key_hash");

            migrationBuilder.CreateIndex(
                name: "ix_outposts_created_at_ticks",
                table: "outposts",
                column: "created_at_ticks");

            migrationBuilder.CreateIndex(
                name: "ix_outposts_enroll_token_hash",
                table: "outposts",
                column: "enroll_token_hash");

            migrationBuilder.CreateIndex(
                name: "ix_outposts_org_id_status",
                table: "outposts",
                columns: new[] { "org_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outposts");

            migrationBuilder.DropColumn(
                name: "outpost_id",
                table: "conversations");
        }
    }
}
