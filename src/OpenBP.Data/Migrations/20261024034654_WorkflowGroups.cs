using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class WorkflowGroups : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "group_id",
            table: "workflows",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "workflow_groups",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                normalized_name = table.Column<string>(type: "text", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_groups", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_workflows_group_id",
            table: "workflows",
            column: "group_id");

        migrationBuilder.CreateIndex(
            name: "IX_workflow_groups_normalized_name",
            table: "workflow_groups",
            column: "normalized_name",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_workflows_workflow_groups_group_id",
            table: "workflows",
            column: "group_id",
            principalTable: "workflow_groups",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_workflows_workflow_groups_group_id",
            table: "workflows");

        migrationBuilder.DropTable(
            name: "workflow_groups");

        migrationBuilder.DropIndex(
            name: "IX_workflows_group_id",
            table: "workflows");

        migrationBuilder.DropColumn(
            name: "group_id",
            table: "workflows");
    }
}
