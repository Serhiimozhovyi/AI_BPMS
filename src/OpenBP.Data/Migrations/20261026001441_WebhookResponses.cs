using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class WebhookResponses : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "webhook_dedupe",
            columns: table => new
            {
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                node_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                status_code = table.Column<int>(type: "integer", nullable: true),
                headers = table.Column<string>(type: "jsonb", nullable: true),
                body = table.Column<byte[]>(type: "bytea", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_webhook_dedupe", x => new { x.workflow_id, x.node_id, x.key });
            });

        migrationBuilder.CreateTable(
            name: "webhook_responses",
            columns: table => new
            {
                execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                status_code = table.Column<int>(type: "integer", nullable: false),
                headers = table.Column<string>(type: "jsonb", nullable: false),
                body = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_webhook_responses", x => x.execution_id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_webhook_dedupe_expires_at",
            table: "webhook_dedupe",
            column: "expires_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "webhook_dedupe");

        migrationBuilder.DropTable(
            name: "webhook_responses");
    }
}
