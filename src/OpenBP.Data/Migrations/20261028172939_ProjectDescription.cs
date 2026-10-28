using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class ProjectDescription : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "description",
            table: "workflow_groups",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "icon",
            table: "workflow_groups",
            type: "text",
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "description",
            table: "workflow_groups");

        migrationBuilder.DropColumn(
            name: "icon",
            table: "workflow_groups");
    }
}
