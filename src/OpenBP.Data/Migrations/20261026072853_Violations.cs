using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class Violations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "violations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                stage_id = table.Column<string>(type: "text", nullable: false),
                rule_id = table.Column<string>(type: "text", nullable: false),
                workflow_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                mode = table.Column<string>(type: "text", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                message = table.Column<string>(type: "text", nullable: true),
                actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                actor_system = table.Column<string>(type: "text", nullable: true),
                reason = table.Column<string>(type: "text", nullable: true),
                context = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_violations", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_violations_execution_id",
            table: "violations",
            column: "execution_id",
            filter: "status = 'open'");

        migrationBuilder.CreateIndex(
            name: "IX_violations_execution_id_stage_id_rule_id",
            table: "violations",
            columns: new[] { "execution_id", "stage_id", "rule_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "violations");
    }
}
