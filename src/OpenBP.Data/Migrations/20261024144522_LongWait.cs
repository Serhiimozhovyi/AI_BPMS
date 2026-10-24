using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class LongWait : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "resume_at",
            table: "executions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "waiting_node_id",
            table: "executions",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "resume_at",
            table: "execution_steps",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "wait_state",
            table: "execution_steps",
            type: "jsonb",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "resume_at",
            table: "executions");

        migrationBuilder.DropColumn(
            name: "waiting_node_id",
            table: "executions");

        migrationBuilder.DropColumn(
            name: "resume_at",
            table: "execution_steps");

        migrationBuilder.DropColumn(
            name: "wait_state",
            table: "execution_steps");
    }
}
