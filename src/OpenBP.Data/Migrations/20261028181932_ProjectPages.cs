using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class ProjectPages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "app_roles",
            table: "workflow_groups",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        migrationBuilder.AddColumn<string>(
            name: "app_role",
            table: "project_members",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "project_pages",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "text", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                definition = table.Column<string>(type: "jsonb", nullable: false),
                roles = table.Column<string>(type: "jsonb", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_pages", x => x.id);
                table.ForeignKey(
                    name: "FK_project_pages_workflow_groups_project_id",
                    column: x => x.project_id,
                    principalTable: "workflow_groups",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_project_pages_project_id_key",
            table: "project_pages",
            columns: new[] { "project_id", "key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "project_pages");

        migrationBuilder.DropColumn(
            name: "app_roles",
            table: "workflow_groups");

        migrationBuilder.DropColumn(
            name: "app_role",
            table: "project_members");
    }
}
