using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class ResumeTokens : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "resume_tokens",
            columns: table => new
            {
                token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                node_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_resume_tokens", x => x.token);
                table.ForeignKey(
                    name: "FK_resume_tokens_executions_execution_id",
                    column: x => x.execution_id,
                    principalTable: "executions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_resume_tokens_execution_id_node_id",
            table: "resume_tokens",
            columns: new[] { "execution_id", "node_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "resume_tokens");
    }
}
