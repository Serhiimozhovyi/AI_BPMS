using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class Notifications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "backup_runs",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                ok = table.Column<bool>(type: "boolean", nullable: false),
                message = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_backup_runs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "execution_stats",
            columns: table => new
            {
                hour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                total = table.Column<int>(type: "integer", nullable: false),
                success = table.Column<int>(type: "integer", nullable: false),
                error = table.Column<int>(type: "integer", nullable: false),
                timed_out = table.Column<int>(type: "integer", nullable: false),
                cancelled = table.Column<int>(type: "integer", nullable: false),
                duration_sum_ms = table.Column<long>(type: "bigint", nullable: false),
                duration_buckets = table.Column<int[]>(type: "integer[]", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_execution_stats", x => new { x.hour, x.workflow_id });
            });

        migrationBuilder.CreateTable(
            name: "notification_channels",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                type = table.Column<string>(type: "text", nullable: false),
                config = table.Column<string>(type: "jsonb", nullable: false),
                events = table.Column<string>(type: "jsonb", nullable: false),
                cooldown_sec = table.Column<int>(type: "integer", nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_channels", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "notifications",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                @event = table.Column<string>(name: "event", type: "text", nullable: false),
                subject = table.Column<string>(type: "text", nullable: false),
                title = table.Column<string>(type: "text", nullable: false),
                details = table.Column<string>(type: "jsonb", nullable: false),
                processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notifications", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "worker_heartbeats",
            columns: table => new
            {
                worker_id = table.Column<string>(type: "text", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_worker_heartbeats", x => x.worker_id);
            });

        migrationBuilder.CreateTable(
            name: "notification_sends",
            columns: table => new
            {
                channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "text", nullable: false),
                sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_sends", x => new { x.channel_id, x.key });
                table.ForeignKey(
                    name: "FK_notification_sends_notification_channels_channel_id",
                    column: x => x.channel_id,
                    principalTable: "notification_channels",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_executions_finished_at",
            table: "executions",
            column: "finished_at");

        migrationBuilder.CreateIndex(
            name: "IX_backup_runs_at",
            table: "backup_runs",
            column: "at");

        migrationBuilder.CreateIndex(
            name: "IX_notifications_created_at",
            table: "notifications",
            column: "created_at",
            filter: "processed_at IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "backup_runs");

        migrationBuilder.DropTable(
            name: "execution_stats");

        migrationBuilder.DropTable(
            name: "notification_sends");

        migrationBuilder.DropTable(
            name: "notifications");

        migrationBuilder.DropTable(
            name: "worker_heartbeats");

        migrationBuilder.DropTable(
            name: "notification_channels");

        migrationBuilder.DropIndex(
            name: "IX_executions_finished_at",
            table: "executions");
    }
}
