using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class Projects : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "project_id",
            table: "mcp_servers",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "project_id",
            table: "credentials",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "project_id",
            table: "api_keys",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "project_members",
            columns: table => new
            {
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_members", x => new { x.project_id, x.user_id });
                table.ForeignKey(
                    name: "FK_project_members_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_project_members_workflow_groups_project_id",
                    column: x => x.project_id,
                    principalTable: "workflow_groups",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Групи стають проєктами: щоб не-адміни не втратили доступ до вже згрупованих сценаріїв, кожен
        // наявний editor/viewer стає учасником кожної наявної групи зі своєю глобальною роллю.
        migrationBuilder.Sql("""
            INSERT INTO project_members (project_id, user_id, role, created_at)
            SELECT g.id, u.id, u.role, now()
            FROM workflow_groups g CROSS JOIN users u
            WHERE u.role IN ('editor', 'viewer');
            """);

        migrationBuilder.CreateIndex(
            name: "IX_mcp_servers_project_id",
            table: "mcp_servers",
            column: "project_id");

        migrationBuilder.CreateIndex(
            name: "IX_credentials_project_id",
            table: "credentials",
            column: "project_id");

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_project_id",
            table: "api_keys",
            column: "project_id");

        migrationBuilder.CreateIndex(
            name: "IX_project_members_user_id",
            table: "project_members",
            column: "user_id");

        migrationBuilder.AddForeignKey(
            name: "FK_api_keys_workflow_groups_project_id",
            table: "api_keys",
            column: "project_id",
            principalTable: "workflow_groups",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_credentials_workflow_groups_project_id",
            table: "credentials",
            column: "project_id",
            principalTable: "workflow_groups",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_mcp_servers_workflow_groups_project_id",
            table: "mcp_servers",
            column: "project_id",
            principalTable: "workflow_groups",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_api_keys_workflow_groups_project_id",
            table: "api_keys");

        migrationBuilder.DropForeignKey(
            name: "FK_credentials_workflow_groups_project_id",
            table: "credentials");

        migrationBuilder.DropForeignKey(
            name: "FK_mcp_servers_workflow_groups_project_id",
            table: "mcp_servers");

        migrationBuilder.DropTable(
            name: "project_members");

        migrationBuilder.DropIndex(
            name: "IX_mcp_servers_project_id",
            table: "mcp_servers");

        migrationBuilder.DropIndex(
            name: "IX_credentials_project_id",
            table: "credentials");

        migrationBuilder.DropIndex(
            name: "IX_api_keys_project_id",
            table: "api_keys");

        migrationBuilder.DropColumn(
            name: "project_id",
            table: "mcp_servers");

        migrationBuilder.DropColumn(
            name: "project_id",
            table: "credentials");

        migrationBuilder.DropColumn(
            name: "project_id",
            table: "api_keys");
    }
}
