using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class ProjectTables : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_data_tables_normalized_name",
            table: "data_tables");

        migrationBuilder.AddColumn<string>(
            name: "constraints",
            table: "data_tables",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'{}'::jsonb");

        migrationBuilder.AddColumn<Guid>(
            name: "project_id",
            table: "data_tables",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_data_tables_project_id_normalized_name",
            table: "data_tables",
            columns: new[] { "project_id", "normalized_name" },
            unique: true)
            .Annotation("Npgsql:NullsDistinct", false);

        migrationBuilder.AddForeignKey(
            name: "FK_data_tables_workflow_groups_project_id",
            table: "data_tables",
            column: "project_id",
            principalTable: "workflow_groups",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_data_tables_workflow_groups_project_id",
            table: "data_tables");

        migrationBuilder.DropIndex(
            name: "IX_data_tables_project_id_normalized_name",
            table: "data_tables");

        migrationBuilder.DropColumn(
            name: "constraints",
            table: "data_tables");

        migrationBuilder.DropColumn(
            name: "project_id",
            table: "data_tables");

        migrationBuilder.CreateIndex(
            name: "IX_data_tables_normalized_name",
            table: "data_tables",
            column: "normalized_name",
            unique: true);
    }
}
