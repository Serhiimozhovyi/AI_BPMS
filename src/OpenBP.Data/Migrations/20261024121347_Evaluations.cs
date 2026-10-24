using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class Evaluations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "evaluations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                node_id = table.Column<string>(type: "text", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_evaluations", x => x.id);
                table.ForeignKey(
                    name: "FK_evaluations_workflows_workflow_id",
                    column: x => x.workflow_id,
                    principalTable: "workflows",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "evaluation_cases",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                input = table.Column<string>(type: "jsonb", nullable: false),
                expected = table.Column<string>(type: "jsonb", nullable: true),
                check = table.Column<string>(type: "text", nullable: false),
                check_config = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_evaluation_cases", x => x.id);
                table.ForeignKey(
                    name: "FK_evaluation_cases_evaluations_evaluation_id",
                    column: x => x.evaluation_id,
                    principalTable: "evaluations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "evaluation_runs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                version_id = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                passed = table.Column<int>(type: "integer", nullable: false),
                total = table.Column<int>(type: "integer", nullable: false),
                results = table.Column<string>(type: "jsonb", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_evaluation_runs", x => x.id);
                table.ForeignKey(
                    name: "FK_evaluation_runs_evaluations_evaluation_id",
                    column: x => x.evaluation_id,
                    principalTable: "evaluations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_evaluation_runs_workflow_versions_version_id",
                    column: x => x.version_id,
                    principalTable: "workflow_versions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_evaluation_cases_evaluation_id_sort_order",
            table: "evaluation_cases",
            columns: new[] { "evaluation_id", "sort_order" });

        migrationBuilder.CreateIndex(
            name: "IX_evaluation_runs_evaluation_id_started_at",
            table: "evaluation_runs",
            columns: new[] { "evaluation_id", "started_at" });

        migrationBuilder.CreateIndex(
            name: "IX_evaluation_runs_version_id",
            table: "evaluation_runs",
            column: "version_id");

        migrationBuilder.CreateIndex(
            name: "IX_evaluations_workflow_id_node_id",
            table: "evaluations",
            columns: new[] { "workflow_id", "node_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "evaluation_cases");

        migrationBuilder.DropTable(
            name: "evaluation_runs");

        migrationBuilder.DropTable(
            name: "evaluations");
    }
}
