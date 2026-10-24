using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class DraftPublish : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "published_version_id",
            table: "workflows",
            type: "uuid",
            nullable: true);

        // Активні сценарії до F6-10 виконували поточну версію — вона й стає опублікованою.
        migrationBuilder.Sql("UPDATE workflows SET published_version_id = current_version_id WHERE active;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "published_version_id",
            table: "workflows");
    }
}
