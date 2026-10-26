using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class Tasks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tasks",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                node_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                description = table.Column<string>(type: "text", nullable: true),
                decisions = table.Column<string>(type: "jsonb", nullable: false),
                fields = table.Column<string>(type: "jsonb", nullable: true),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                assignee_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                assignee_value = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                decision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                data = table.Column<string>(type: "jsonb", nullable: true),
                completed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                completed_by_api_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                cancelled_by_execution_id = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tasks", x => x.id);
                table.ForeignKey(
                    name: "FK_tasks_executions_execution_id",
                    column: x => x.execution_id,
                    principalTable: "executions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_tasks_created_at",
            table: "tasks",
            column: "created_at");

        migrationBuilder.CreateIndex(
            name: "IX_tasks_execution_id_node_id",
            table: "tasks",
            columns: new[] { "execution_id", "node_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_tasks_status_assignee_user_id",
            table: "tasks",
            columns: new[] { "status", "assignee_user_id" });

        migrationBuilder.CreateIndex(
            name: "IX_tasks_token",
            table: "tasks",
            column: "token",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "tasks");
    }
}
