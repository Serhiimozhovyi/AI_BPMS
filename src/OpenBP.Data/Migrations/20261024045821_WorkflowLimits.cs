using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class WorkflowLimits : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "activated_at",
            table: "workflows",
            type: "timestamp with time zone",
            nullable: true);

        // Уже активні сценарії: серія помилок запобіжника (F6-06) рахується від останньої зміни.
        migrationBuilder.Sql("UPDATE workflows SET activated_at = updated_at WHERE active");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "scheduled_at",
            table: "executions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_executions_workflow_id_scheduled_at",
            table: "executions",
            columns: new[] { "workflow_id", "scheduled_at" },
            filter: "scheduled_at IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_executions_workflow_id_scheduled_at",
            table: "executions");

        migrationBuilder.DropColumn(
            name: "activated_at",
            table: "workflows");

        migrationBuilder.DropColumn(
            name: "scheduled_at",
            table: "executions");
    }
}
