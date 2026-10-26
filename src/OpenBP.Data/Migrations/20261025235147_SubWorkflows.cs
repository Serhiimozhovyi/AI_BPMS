using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class SubWorkflows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "parent_execution_id",
            table: "executions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "parent_node_id",
            table: "executions",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_executions_parent_execution_id",
            table: "executions",
            column: "parent_execution_id",
            filter: "parent_execution_id IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_executions_parent_execution_id",
            table: "executions");

        migrationBuilder.DropColumn(
            name: "parent_execution_id",
            table: "executions");

        migrationBuilder.DropColumn(
            name: "parent_node_id",
            table: "executions");
    }
}
