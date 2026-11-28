using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "factories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    owner_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_factories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    current_step_index = table.Column<int>(type: "INTEGER", nullable: false),
                    current_step_name = table.Column<string>(type: "TEXT", nullable: true),
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    inputs_json = table.Column<string>(type: "TEXT", nullable: false),
                    audit_json = table.Column<string>(type: "TEXT", nullable: false),
                    attempts_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at_ticks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    org_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    owner_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    factory_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    definition_json = table.Column<string>(type: "TEXT", nullable: false),
                    autonomous_delivery = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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