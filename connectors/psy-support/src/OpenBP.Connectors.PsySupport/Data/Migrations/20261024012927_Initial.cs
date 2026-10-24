using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenBP.Connectors.PsySupport.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity = table.Column<string>(type: "text", nullable: true),
                    entity_id = table.Column<long>(type: "bigint", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    slot_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<string>(type: "text", nullable: true),
                    moved_from_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bookings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bst_bookings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    moved_from_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bst_bookings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bst_sessions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    psychologist_id = table.Column<long>(type: "bigint", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    format = table.Column<string>(type: "text", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    notice = table.Column<string>(type: "text", nullable: true),
                    notice_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notice_sent_text = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bst_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "counters",
                columns: table => new
                {
                    metric = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_counters", x => x.metric);
                });

            migrationBuilder.CreateTable(
                name: "event_registrations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    feedback = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_registrations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    format = table.Column<string>(type: "text", nullable: true),
                    host = table.Column<string>(type: "text", nullable: true),
                    contact = table.Column<string>(type: "text", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    notice = table.Column<string>(type: "text", nullable: true),
                    notice_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notice_sent_text = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_map",
                columns: table => new
                {
                    sheet = table.Column<string>(type: "text", nullable: false),
                    old_key = table.Column<string>(type: "text", nullable: false),
                    new_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_map", x => new { x.sheet, x.old_key });
                });

            migrationBuilder.CreateTable(
                name: "invites",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    staff_account_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "materials",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    link = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    views = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_materials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    reply_markup = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "psychologists",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_psychologists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reminders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    offset_min = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "slot_templates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    psychologist_id = table.Column<long>(type: "bigint", nullable: false),
                    weekday = table.Column<int>(type: "integer", nullable: false),
                    time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    duration_min = table.Column<int>(type: "integer", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_slot_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "slots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    psychologist_id = table.Column<long>(type: "bigint", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_min = table.Column<int>(type: "integer", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_slots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_accounts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    role = table.Column<string>(type: "text", nullable: false),
                    psychologist_id = table.Column<long>(type: "bigint", nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_logins = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    telegram_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.telegram_id);
                });

            migrationBuilder.CreateTable(
                name: "waitlist",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    offered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waitlist", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_telegram_id_status",
                table: "bookings",
                columns: new[] { "telegram_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_bookings_slot_active",
                table: "bookings",
                column: "slot_id",
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_bst_bookings_session_id_telegram_id",
                table: "bst_bookings",
                columns: new[] { "session_id", "telegram_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_registrations_event_id_telegram_id",
                table: "event_registrations",
                columns: new[] { "event_id", "telegram_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invites_token_hash",
                table: "invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_created_at",
                table: "outbox",
                column: "created_at",
                filter: "sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_psychologists_telegram_id",
                table: "psychologists",
                column: "telegram_id",
                unique: true,
                filter: "telegram_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reminders_kind_target_id_offset_min",
                table: "reminders",
                columns: new[] { "kind", "target_id", "offset_min" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_slots_psychologist_id_starts_at",
                table: "slots",
                columns: new[] { "psychologist_id", "starts_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_accounts_email",
                table: "staff_accounts",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_phone",
                table: "users",
                column: "phone",
                unique: true,
                filter: "deleted_at IS NULL AND phone IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_session_id_telegram_id",
                table: "waitlist",
                columns: new[] { "session_id", "telegram_id" },
                unique: true,
                filter: "status IN ('waiting', 'offered')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "bookings");

            migrationBuilder.DropTable(
                name: "bst_bookings");

            migrationBuilder.DropTable(
                name: "bst_sessions");

            migrationBuilder.DropTable(
                name: "counters");

            migrationBuilder.DropTable(
                name: "event_registrations");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "import_map");

            migrationBuilder.DropTable(
                name: "invites");

            migrationBuilder.DropTable(
                name: "materials");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "psychologists");

            migrationBuilder.DropTable(
                name: "reminders");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "slot_templates");

            migrationBuilder.DropTable(
                name: "slots");

            migrationBuilder.DropTable(
                name: "staff_accounts");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "waitlist");
        }
    }
}
