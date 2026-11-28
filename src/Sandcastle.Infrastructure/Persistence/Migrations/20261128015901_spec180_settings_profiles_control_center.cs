using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec180_settings_profiles_control_center : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    prompt_pack = table.Column<string>(type: "TEXT", nullable: true),
                    agent_type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    runtime_profile = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    llm_profile_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    allowed_tools_json = table.Column<string>(type: "TEXT", nullable: true),
                    skills_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "llm_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    provider_connection_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    temperature = table.Column<double>(type: "REAL", nullable: true),
                    role_models_json = table.Column<string>(type: "TEXT", nullable: true),
                    fallback_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_llm_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    base_url = table.Column<string>(type: "TEXT", nullable: true),
                    secret_ref = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_connections", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_profiles_org_id_name",
                table: "agent_profiles",
                columns: new[] { "org_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_profiles_owner_id_name",
                table: "agent_profiles",
                columns: new[] { "owner_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_llm_profiles_org_id_name",
                table: "llm_profiles",
                columns: new[] { "org_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_llm_profiles_owner_id_name",
                table: "llm_profiles",
                columns: new[] { "owner_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_provider_connections_org_id_provider",
                table: "provider_connections",
                columns: new[] { "org_id", "provider" });

            migrationBuilder.CreateIndex(
                name: "ix_provider_connections_owner_id_provider",
                table: "provider_connections",
                columns: new[] { "owner_id", "provider" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_profiles");

            migrationBuilder.DropTable(
                name: "llm_profiles");

            migrationBuilder.DropTable(
                name: "provider_connections");
        }
    }
}