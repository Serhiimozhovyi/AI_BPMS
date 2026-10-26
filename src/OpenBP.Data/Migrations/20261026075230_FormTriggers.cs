using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class FormTriggers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_webhooks_path",
            table: "webhooks");

        migrationBuilder.AddColumn<string>(
            name: "kind",
            table: "webhooks",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "webhook");

        migrationBuilder.CreateTable(
            name: "binary_files",
            columns: table => new
            {
                id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                size = table.Column<long>(type: "bigint", nullable: false),
                sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                content = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_binary_files", x => x.id);
                table.ForeignKey(
                    name: "FK_binary_files_executions_execution_id",
                    column: x => x.execution_id,
                    principalTable: "executions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_webhooks_kind_path",
            table: "webhooks",
            columns: new[] { "kind", "path" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_binary_files_created_at",
            table: "binary_files",
            column: "created_at");

        migrationBuilder.CreateIndex(
            name: "IX_binary_files_execution_id",
            table: "binary_files",
            column: "execution_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "binary_files");

        migrationBuilder.DropIndex(
            name: "IX_webhooks_kind_path",
            table: "webhooks");

        migrationBuilder.DropColumn(
            name: "kind",
            table: "webhooks");

        migrationBuilder.CreateIndex(
            name: "IX_webhooks_path",
            table: "webhooks",
            column: "path",
            unique: true);
    }
}
