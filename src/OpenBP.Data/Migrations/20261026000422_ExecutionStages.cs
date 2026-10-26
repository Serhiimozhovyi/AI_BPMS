using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class ExecutionStages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "execution_stages",
            columns: table => new
            {
                execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                stage_id = table.Column<string>(type: "text", nullable: false),
                position = table.Column<int>(type: "integer", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                exited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_execution_stages", x => new { x.execution_id, x.stage_id });
            });

        migrationBuilder.CreateIndex(
            name: "IX_execution_stages_due_at",
            table: "execution_stages",
            column: "due_at",
            filter: "status = 'active'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "execution_stages");
    }
}
