using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class CorrelationKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "correlation_key",
            table: "executions",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_executions_workflow_id_correlation_key",
            table: "executions",
            columns: new[] { "workflow_id", "correlation_key" },
            filter: "correlation_key IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_executions_workflow_id_correlation_key",
            table: "executions");

        migrationBuilder.DropColumn(
            name: "correlation_key",
            table: "executions");
    }
}
