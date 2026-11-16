using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec157_workspace_items : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    body_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_items_org_id_kind",
                table: "workspace_items",
                columns: new[] { "org_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_items_owner_id_kind",
                table: "workspace_items",
                columns: new[] { "owner_id", "kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_items");
        }
    }
}