using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "api_keys",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                hash = table.Column<string>(type: "text", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_keys", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "credentials",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                type = table.Column<string>(type: "text", nullable: false),
                data_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_credentials", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "execution_steps",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                node_id = table.Column<string>(type: "text", nullable: false),
                run_index = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                input = table.Column<string>(type: "jsonb", nullable: true),
                output = table.Column<string>(type: "jsonb", nullable: true),
                error = table.Column<string>(type: "text", nullable: true),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_execution_steps", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "executions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                version_id = table.Column<Guid>(type: "uuid", nullable: false),
                mode = table.Column<string>(type: "text", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_executions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "jobs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                locked_by = table.Column<string>(type: "text", nullable: true),
                locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                attempts = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_jobs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "mcp_servers",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                transport = table.Column<string>(type: "text", nullable: false),
                config = table.Column<string>(type: "jsonb", nullable: false),
                credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                tools_cache = table.Column<string>(type: "jsonb", nullable: true),
                tools_cached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_mcp_servers", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "node_state",
            columns: table => new
            {
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                node_id = table.Column<string>(type: "text", nullable: false),
                state = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_node_state", x => new { x.workflow_id, x.node_id });
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                email = table.Column<string>(type: "text", nullable: false),
                password_hash = table.Column<string>(type: "text", nullable: false),
                role = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "webhooks",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                node_id = table.Column<string>(type: "text", nullable: false),
                method = table.Column<string>(type: "text", nullable: false),
                path = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_webhooks", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "workflow_versions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                definition = table.Column<string>(type: "jsonb", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_versions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "workflows",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                active = table.Column<bool>(type: "boolean", nullable: false),
                current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflows", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_hash",
            table: "api_keys",
            column: "hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_execution_steps_execution_id",
            table: "execution_steps",
            column: "execution_id");

        migrationBuilder.CreateIndex(
            name: "IX_executions_workflow_id_started_at",
            table: "executions",
            columns: new[] { "workflow_id", "started_at" });

        migrationBuilder.CreateIndex(
            name: "IX_jobs_run_at",
            table: "jobs",
            column: "run_at",
            filter: "locked_by IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_users_email",
            table: "users",
            column: "email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_webhooks_path",
            table: "webhooks",
            column: "path",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_workflow_versions_workflow_id_version",
            table: "workflow_versions",
            columns: new[] { "workflow_id", "version" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "api_keys");

        migrationBuilder.DropTable(
            name: "credentials");

        migrationBuilder.DropTable(
            name: "execution_steps");

        migrationBuilder.DropTable(
            name: "executions");

        migrationBuilder.DropTable(
            name: "jobs");

        migrationBuilder.DropTable(
            name: "mcp_servers");

        migrationBuilder.DropTable(
            name: "node_state");

        migrationBuilder.DropTable(
            name: "users");

        migrationBuilder.DropTable(
            name: "webhooks");

        migrationBuilder.DropTable(
            name: "workflow_versions");

        migrationBuilder.DropTable(
            name: "workflows");
    }
}
