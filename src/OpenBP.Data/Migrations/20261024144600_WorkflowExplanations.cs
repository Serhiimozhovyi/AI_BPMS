using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class WorkflowExplanations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "workflow_explanations",
            columns: table => new
            {
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                text = table.Column<string>(type: "text", nullable: true),
                error = table.Column<string>(type: "text", nullable: true),
                definition_hash = table.Column<string>(type: "text", nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_explanations", x => x.workflow_id);
                table.ForeignKey(
                    name: "FK_workflow_explanations_workflows_workflow_id",
                    column: x => x.workflow_id,
                    principalTable: "workflows",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "workflow_explanations");
    }
}
