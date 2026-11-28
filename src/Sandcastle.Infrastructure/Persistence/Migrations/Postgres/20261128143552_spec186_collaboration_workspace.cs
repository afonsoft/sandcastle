using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec186_collaboration_workspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collab_channel_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_user_id = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reactions_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collab_channel_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collab_channel_reads",
                columns: table => new
                {
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collab_channel_reads", x => new { x.channel_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "collab_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    pinned_user_ids_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collab_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collab_note_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collab_note_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collab_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    knowledge_link = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collab_notes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collab_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    deep_link = table.Column<string>(type: "text", nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collab_notifications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collab_channel_messages_channel_id_created_at",
                table: "collab_channel_messages",
                columns: new[] { "channel_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_collab_channels_org_id",
                table: "collab_channels",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ix_collab_note_versions_note_id_version",
                table: "collab_note_versions",
                columns: new[] { "note_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_collab_notes_org_id",
                table: "collab_notes",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ix_collab_notifications_user_id_created_at",
                table: "collab_notifications",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collab_channel_messages");

            migrationBuilder.DropTable(
                name: "collab_channel_reads");

            migrationBuilder.DropTable(
                name: "collab_channels");

            migrationBuilder.DropTable(
                name: "collab_note_versions");

            migrationBuilder.DropTable(
                name: "collab_notes");

            migrationBuilder.DropTable(
                name: "collab_notifications");
        }
    }
}