using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBP.Data.Migrations;

/// <inheritdoc />
public partial class OAuthStates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "oauth_states",
            columns: table => new
            {
                state = table.Column<string>(type: "text", nullable: false),
                credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                code_verifier = table.Column<string>(type: "text", nullable: false),
                redirect_uri = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_oauth_states", x => x.state);
            });

        migrationBuilder.CreateIndex(
            name: "IX_oauth_states_created_at",
            table: "oauth_states",
            column: "created_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "oauth_states");
    }
}
