using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class spec180_profile_selection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "agent_profile_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "llm_profile_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "agent_profile_id",
                table: "automations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "llm_profile_id",
                table: "automations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agent_profile_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "llm_profile_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "agent_profile_id",
                table: "automations");

            migrationBuilder.DropColumn(
                name: "llm_profile_id",
                table: "automations");
        }
    }
}