using System.Text;
using Microsoft.EntityFrameworkCore;

namespace OpenBP.Connectors.PsySupport.Data;

/// <summary>БД застосунку (docs/psy-support.md §3); колонки й індекси — snake_case.</summary>
public sealed class PsyDbContext(DbContextOptions<PsyDbContext> options) : DbContext(options)
{
    /// <summary>Частковий унікальний індекс «один слот — один активний запис» (правило 4.2).</summary>
    public const string OneActiveBookingPerSlot = "ux_bookings_slot_active";

    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<PsychologistEntity> Psychologists => Set<PsychologistEntity>();
    public DbSet<StaffAccountEntity> StaffAccounts => Set<StaffAccountEntity>();
    public DbSet<InviteEntity> Invites => Set<InviteEntity>();
    public DbSet<SlotEntity> Slots => Set<SlotEntity>();
    public DbSet<BookingEntity> Bookings => Set<BookingEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<EventRegistrationEntity> EventRegistrations => Set<EventRegistrationEntity>();
    public DbSet<BstSessionEntity> BstSessions => Set<BstSessionEntity>();
    public DbSet<BstBookingEntity> BstBookings => Set<BstBookingEntity>();
    public DbSet<WaitlistEntity> Waitlist => Set<WaitlistEntity>();
    public DbSet<MaterialEntity> Materials => Set<MaterialEntity>();
    public DbSet<ReminderEntity> Reminders => Set<ReminderEntity>();
    public DbSet<SlotTemplateEntity> SlotTemplates => Set<SlotTemplateEntity>();
    public DbSet<CounterEntity> Counters => Set<CounterEntity>();
    public DbSet<OutboxEntity> Outbox => Set<OutboxEntity>();
    public DbSet<AuditEntity> Audit => Set<AuditEntity>();
    public DbSet<ImportMapEntity> ImportMap => Set<ImportMapEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SettingEntity>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
        });
        b.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.TelegramId);
            e.Property(x => x.TelegramId).ValueGeneratedNever();
            e.HasIndex(x => x.Phone).IsUnique().HasFilter("deleted_at IS NULL AND phone IS NOT NULL");
        });
        b.Entity<PsychologistEntity>(e =>
        {
            e.ToTable("psychologists");
            e.HasIndex(x => x.TelegramId).IsUnique().HasFilter("telegram_id IS NOT NULL");
        });
        b.Entity<StaffAccountEntity>(e =>
        {
            e.ToTable("staff_accounts");
            e.HasIndex(x => x.Email).IsUnique();
        });
        b.Entity<InviteEntity>(e =>
        {
            e.ToTable("invites");
            e.HasIndex(x => x.TokenHash).IsUnique();
        });
        b.Entity<SlotEntity>(e =>
        {
            e.ToTable("slots");
            e.HasIndex(x => new { x.PsychologistId, x.StartsAt }).IsUnique();
        });
        b.Entity<BookingEntity>(e =>
        {
            e.ToTable("bookings");
            e.HasIndex(x => x.SlotId).IsUnique().HasFilter("status = 'active'").HasDatabaseName(OneActiveBookingPerSlot);
            e.HasIndex(x => new { x.TelegramId, x.Status });
        });
        b.Entity<EventEntity>().ToTable("events");
        b.Entity<EventRegistrationEntity>(e =>
        {
            e.ToTable("event_registrations");
            e.HasIndex(x => new { x.EventId, x.TelegramId }).IsUnique();
        });
        b.Entity<BstSessionEntity>().ToTable("bst_sessions");
        b.Entity<BstBookingEntity>(e =>
        {
            e.ToTable("bst_bookings");
            e.HasIndex(x => new { x.SessionId, x.TelegramId }).IsUnique();
        });
        b.Entity<WaitlistEntity>(e =>
        {
            e.ToTable("waitlist");
            e.HasIndex(x => new { x.SessionId, x.TelegramId }).IsUnique().HasFilter("status IN ('waiting', 'offered')");
        });
        b.Entity<MaterialEntity>().ToTable("materials");
        b.Entity<ReminderEntity>(e =>
        {
            e.ToTable("reminders");
            e.HasIndex(x => new { x.Kind, x.TargetId, x.OffsetMin }).IsUnique();
        });
        b.Entity<SlotTemplateEntity>().ToTable("slot_templates");
        b.Entity<CounterEntity>(e =>
        {
            e.ToTable("counters");
            e.HasKey(x => x.Metric);
        });
        b.Entity<OutboxEntity>(e =>
        {
            e.ToTable("outbox");
            e.Property(x => x.ReplyMarkup).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt).HasFilter("sent_at IS NULL");
        });
        b.Entity<AuditEntity>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => x.At);
        });
        b.Entity<ImportMapEntity>(e =>
        {
            e.ToTable("import_map");
            e.HasKey(x => new { x.Sheet, x.OldKey });
        });

        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
            {
                p.SetColumnName(ToSnake(p.Name));
            }
        }
    }

    private static string ToSnake(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }
}
