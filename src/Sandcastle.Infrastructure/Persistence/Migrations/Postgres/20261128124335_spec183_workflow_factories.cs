using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec183_workflow_factories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "factory_id",
                table: "automations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "factories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_factories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_step_index = table.Column<int>(type: "integer", nullable: false),
                    current_step_name = table.Column<string>(type: "text", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    inputs_json = table.Column<string>(type: "text", nullable: false),
                    audit_json = table.Column<string>(type: "text", nullable: false),
                    attempts_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<string>(type: "text", nullable: false),
                    factory_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    definition_json = table.Column<string>(type: "text", nullable: false),
                    autonomous_delivery = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflows", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automations_factory_id",
                table: "automations",
                column: "factory_id");

            migrationBuilder.CreateIndex(
                name: "ix_factories_owner_user_id",
                table: "factories",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_owner_user_id",
                table: "workflow_runs",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_status",
                table: "workflow_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_workflow_id_created_at_ticks",
                table: "workflow_runs",
                columns: new[] { "workflow_id", "created_at_ticks" });

            migrationBuilder.CreateIndex(
                name: "ix_workflows_factory_id",
                table: "workflows",
                column: "factory_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflows_owner_user_id",
                table: "workflows",
                column: "owner_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factories");

            migrationBuilder.DropTable(
                name: "workflow_runs");

            migrationBuilder.DropTable(
                name: "workflows");

            migrationBuilder.DropIndex(
                name: "ix_automations_factory_id",
                table: "automations");

            migrationBuilder.DropColumn(
                name: "factory_id",
                table: "automations");
        }
    }
}