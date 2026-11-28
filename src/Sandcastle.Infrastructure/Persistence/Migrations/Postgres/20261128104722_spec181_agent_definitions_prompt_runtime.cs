using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec181_agent_definitions_prompt_runtime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: true),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: false),
                    tools_json = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    model_profile = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    skills_json = table.Column<string>(type: "text", nullable: true),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    permission_preset = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "prompt_packs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: true),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    base_prompt = table.Column<string>(type: "text", nullable: false),
                    harness_rules = table.Column<string>(type: "text", nullable: true),
                    plan_mode_prompt = table.Column<string>(type: "text", nullable: true),
                    build_mode_prompt = table.Column<string>(type: "text", nullable: true),
                    advisor_prompt = table.Column<string>(type: "text", nullable: true),
                    permission_judge_prompt = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_packs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_definitions_scope_owner_id_org_id_name",
                table: "agent_definitions",
                columns: new[] { "scope", "owner_id", "org_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_packs_pack_id_version",
                table: "prompt_packs",
                columns: new[] { "pack_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_prompt_packs_scope_owner_id_org_id_name_active",
                table: "prompt_packs",
                columns: new[] { "scope", "owner_id", "org_id", "name", "active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_definitions");

            migrationBuilder.DropTable(
                name: "prompt_packs");
        }
    }
}