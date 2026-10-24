using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class AuditLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "audit_log",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: true),
                api_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                action = table.Column<string>(type: "text", nullable: false),
                entity_type = table.Column<string>(type: "text", nullable: false),
                entity_id = table.Column<string>(type: "text", nullable: true),
                details = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_log", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_audit_log_at",
            table: "audit_log",
            column: "at");

        migrationBuilder.CreateIndex(
            name: "IX_audit_log_entity_type_entity_id",
            table: "audit_log",
            columns: new[] { "entity_type", "entity_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_log");
    }
}
