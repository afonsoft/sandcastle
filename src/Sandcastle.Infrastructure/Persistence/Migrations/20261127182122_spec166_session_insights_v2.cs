using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sandcastle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class spec166_session_insights_v2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "action_items_json",
                table: "session_insights",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "session_insights",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "llm_analysis_failed",
                table: "session_insights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "llm_summary",
                table: "session_insights",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "size_tier",
                table: "session_insights",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "timeline_json",
                table: "session_insights",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "action_items_json",
                table: "session_insights");

            migrationBuilder.DropColumn(
                name: "category",
                table: "session_insights");

            migrationBuilder.DropColumn(
                name: "llm_analysis_failed",
                table: "session_insights");

            migrationBuilder.DropColumn(
                name: "llm_summary",
                table: "session_insights");

            migrationBuilder.DropColumn(
                name: "size_tier",
                table: "session_insights");

            migrationBuilder.DropColumn(
                name: "timeline_json",
                table: "session_insights");
        }
    }
}