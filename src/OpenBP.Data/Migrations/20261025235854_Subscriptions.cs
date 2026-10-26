using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class Subscriptions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "subscriptions",
            columns: table => new
            {
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                node_id = table.Column<string>(type: "text", nullable: false),
                server_id = table.Column<string>(type: "text", nullable: false),
                resource_uri = table.Column<string>(type: "text", nullable: false),
                worker_id = table.Column<string>(type: "text", nullable: true),
                leased_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_subscriptions", x => new { x.workflow_id, x.node_id });
                table.ForeignKey(
                    name: "FK_subscriptions_workflows_workflow_id",
                    column: x => x.workflow_id,
                    principalTable: "workflows",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_subscriptions_leased_until",
            table: "subscriptions",
            column: "leased_until");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "subscriptions");
    }
}
