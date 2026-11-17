using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec161_automation_robustness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "consecutive_failures",
                table: "automations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "misfire_policy",
                table: "automations",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "run-once");

            migrationBuilder.AddColumn<bool>(
                name: "missed",
                table: "automation_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consecutive_failures",
                table: "automations");

            migrationBuilder.DropColumn(
                name: "misfire_policy",
                table: "automations");

            migrationBuilder.DropColumn(
                name: "missed",
                table: "automation_runs");
        }
    }
}