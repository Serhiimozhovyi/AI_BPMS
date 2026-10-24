using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Import;

/// <summary>Підсумок одного листа: без персональних даних, лише кількості й коди причин.</summary>
public sealed class SheetReport
{
    public int Rows { get; set; }
    public int Imported { get; set; }
    public int Existing { get; set; }
    public int Skipped { get; set; }
    public Dictionary<string, int> Reasons { get; } = [];
    public string? Note { get; set; }

    internal void Skip(string reason)
    {
        Skipped++;
        Reasons[reason] = Reasons.GetValueOrDefault(reason) + 1;
    }
}

/// <summary>Звіт імпорту.</summary>
public sealed class ImportReport
{
    public Dictionary<string, SheetReport> Sheets { get; } = [];
    public bool Failed { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>
/// Одноразовий імпорт CSV-вивантажень листів таблиці (docs/psy-support.md §9). Ідемпотентний: старі ключі зберігаються в
/// <c>import_map</c>, і повторний запуск на тих самих файлах нічого не дублює. Лист <c>log</c> не переноситься.
/// </summary>
public sealed class SheetImporter(PsyDbContext db, PsyClock clock, SettingsService settings)
{
    /// <summary>Листи в порядку залежностей.</summary>
    public static readonly string[] SheetOrder =
        ["settings", "counters", "psychologists", "users", "slots", "bookings", "events", "registrations", "materials", "bst_sessions", "bst_bookings", "waitlist"];

    private readonly DateTimeOffset _startedAt = clock.Now;

    public static async Task<ImportReport> RunAsync(IServiceProvider services, string dir, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var importer = ActivatorUtilities.CreateInstance<SheetImporter>(scope.ServiceProvider);
        return await importer.ImportAsync(dir, ct).ConfigureAwait(false);
    }

    public async Task<ImportReport> ImportAsync(string dir, CancellationToken ct = default)
    {
        var report = new ImportReport();
        if (!Directory.Exists(dir))
        {
            report.Failed = true;
            report.Sheets["_"] = new SheetReport { Note = "папки не існує" };
            return report;
        }

        foreach (var sheet in SheetOrder)
        {
            var path = Path.Combine(dir, sheet + ".csv");
            var r = report.Sheets[sheet] = new SheetReport();
            if (!File.Exists(path))
            {
                r.Note = "файлу немає";
                continue;
            }

            var rows = Csv.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            r.Rows = rows.Count;
            for (var i = 0; i < rows.Count; i++)
            {
                try
                {
                    var outcome = await ImportRowAsync(sheet, rows[i], i, ct).ConfigureAwait(false);
                    if (outcome is null)
                    {
                        r.Imported++;
                    }
                    else if (outcome == Exists)
                    {
                        r.Existing++;
                    }
                    else
                    {
                        r.Skip(outcome);
                    }
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                    r.Skip("db_error");
                }
            }
        }

        report.Sheets["bookings"].Imported += await BookedBySlotsAsync(ct).ConfigureAwait(false);
        if (File.Exists(Path.Combine(dir, "log.csv")))
        {
            report.Sheets["log"] = new SheetReport { Note = "історію дій не переносимо" };
        }

        db.Audit.Add(new AuditEntity
        {
            At = clock.Now,
            Actor = "system",
            Action = "import.completed",
            Details = JsonSerializer.Serialize(report.Sheets.ToDictionary(s => s.Key, s => new { s.Value.Imported, s.Value.Skipped })),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return report;
    }

    private const string Exists = "exists";

    private TimeZoneInfo Zone => clock.Zone;

    /// <returns><c>null</c> — імпортовано; <see cref="Exists"/> — уже було; інакше — код причини пропуску.</returns>
    private Task<string?> ImportRowAsync(string sheet, IReadOnlyDictionary<string, string> row, int index, CancellationToken ct) => sheet switch
    {
        "settings" => SettingAsync(row, ct),
        "counters" => CounterAsync(row, ct),
        "psychologists" => PsychologistAsync(row, ct),
        "users" => UserAsync(row, ct),
        "slots" => SlotAsync(row, ct),
        "bookings" => BookingAsync(row, ct),
        "events" => EventAsync(row, ct),
        "registrations" => RegistrationAsync(row, ct),
        "materials" => MaterialAsync(row, index, ct),
        "bst_sessions" => SessionAsync(row, ct),
        "bst_bookings" => BstBookingAsync(row, ct),
        "waitlist" => WaitlistAsync(row, ct),
        _ => Task.FromResult<string?>("unknown_sheet"),
    };

    private static string Get(IReadOnlyDictionary<string, string> row, string column) => row.TryGetValue(column, out var v) ? v : "";

    private async Task<long?> MappedAsync(string sheet, string oldKey, CancellationToken ct) =>
        await db.ImportMap.Where(m => m.Sheet == sheet && m.OldKey == oldKey).Select(m => (long?)m.NewId).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task RememberAsync(string sheet, string oldKey, long newId, CancellationToken ct)
    {
        db.ImportMap.Add(new ImportMapEntity { Sheet = sheet, OldKey = oldKey, NewId = newId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> SettingAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var key = Get(row, "key");
        if (key.Length == 0)
        {
            return "no_key";
        }

        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        if (existing is not null && existing.Value == Get(row, "value"))
        {
            return Exists;
        }

        if (existing is null)
        {
            db.Settings.Add(existing = new SettingEntity { Key = key, Value = "" });
        }

        existing.Value = Get(row, "value");
        existing.UpdatedAt = clock.Now;
        existing.UpdatedBy = "import";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> CounterAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var metric = Get(row, "metric");
        if (metric.Length == 0 || SheetValues.Long(Get(row, "value")) is not { } value)
        {
            return "bad_counter";
        }

        var existing = await db.Counters.FirstOrDefaultAsync(c => c.Metric == metric, ct).ConfigureAwait(false);
        if (existing?.Value == value)
        {
            return Exists;
        }

        if (existing is null)
        {
            db.Counters.Add(existing = new CounterEntity { Metric = metric });
        }

        existing.Value = value;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> PsychologistAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var pid = Get(row, "pid");
        if (pid.Length == 0 || Get(row, "name").Length == 0)
        {
            return "no_pid_or_name";
        }

        if (await MappedAsync("psychologists", pid, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        var tid = SheetValues.Long(Get(row, "telegram_id"));
        if (tid is not null && await db.Psychologists.AnyAsync(p => p.TelegramId == tid, ct).ConfigureAwait(false))
        {
            tid = null;
        }

        var p = new PsychologistEntity
        {
            Name = Get(row, "name"),
            Phone = UserService.NormalizePhone(Get(row, "phone")),
            Email = Get(row, "email") is { Length: > 0 } e ? e : null,
            TelegramId = tid,
            Active = Get(row, "active").Length == 0 || SheetValues.Yes(Get(row, "active")),
            CreatedAt = clock.Now,
        };
        db.Psychologists.Add(p);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RememberAsync("psychologists", pid, p.Id, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> UserAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        if (SheetValues.Long(Get(row, "telegram_id")) is not { } tid)
        {
            return "no_telegram_id";
        }

        if (await db.Users.AnyAsync(u => u.TelegramId == tid, ct).ConfigureAwait(false))
        {
            return Exists;
        }

        var phone = UserService.NormalizePhone(Get(row, "phone"));
        if (phone is not null && await db.Users.AnyAsync(u => u.Phone == phone && u.DeletedAt == null, ct).ConfigureAwait(false))
        {
            phone = null; // той самий номер у кількох рядках — лишаємо його першому
        }

        db.Users.Add(new UserEntity
        {
            TelegramId = tid,
            Name = Get(row, "name") is { Length: > 0 } n ? n : null,
            Phone = phone,
            RegisteredAt = SheetValues.Moment(Get(row, "registered_at"), Zone) ?? _startedAt,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return null;
    }

    private static string SlotKey(IReadOnlyDictionary<string, string> row, string pidColumn) =>
        Get(row, "key") is { Length: > 0 } key ? key : $"{Get(row, pidColumn)}|{Get(row, "date")}|{Get(row, "time")}";

    private async Task<string?> SlotAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var key = SlotKey(row, "psychologist");
        if (await MappedAsync("slots", key, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        if (await MappedAsync("psychologists", Get(row, "psychologist"), ct).ConfigureAwait(false) is not { } pid)
        {
            return "unknown_psychologist";
        }

        if (SheetValues.Date(Get(row, "date")) is not { } date || SheetValues.Time(Get(row, "time")) is not { } time)
        {
            return "bad_date_or_time";
        }

        var startsAt = SheetValues.At(date, time, Zone);
        var slot = await db.Slots.FirstOrDefaultAsync(s => s.PsychologistId == pid && s.StartsAt == startsAt, ct).ConfigureAwait(false);
        if (slot is null)
        {
            slot = new SlotEntity
            {
                PsychologistId = pid,
                StartsAt = startsAt,
                DurationMin = await settings.GetIntAsync(SettingsService.SlotDurationMin, ct).ConfigureAwait(false),
                Format = Get(row, "format") is { Length: > 0 } f ? f : "онлайн",
                Status = Get(row, "status").ToLowerInvariant() is "вільний" or "зайнятий" or "" ? SlotStatuses.Open : SlotStatuses.Closed,
                Source = SlotSources.Manual,
                CreatedAt = clock.Now,
            };
            db.Slots.Add(slot);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await RememberAsync("slots", key, slot.Id, ct).ConfigureAwait(false);

        // «зайнятий» без рядка в bookings: запис береться з колонки booked_by (лист bookings імпортується далі й має перевагу).
        if (Get(row, "status").ToLowerInvariant() == "зайнятий" && SheetValues.Long(Get(row, "booked_by")) is { } bookedBy)
        {
            db.ImportMap.Add(new ImportMapEntity { Sheet = "slot_booked_by", OldKey = key, NewId = bookedBy });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string?> BookingAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var id = Get(row, "booking_id");
        if (id.Length == 0)
        {
            return "no_booking_id";
        }

        if (await MappedAsync("bookings", id, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        if (await MappedAsync("slots", Get(row, "slot_key"), ct).ConfigureAwait(false) is not { } slotId)
        {
            return "unknown_slot";
        }

        var status = Get(row, "status").ToLowerInvariant() switch
        {
            "активний" => BookingStatuses.Active,
            "скасовано" => BookingStatuses.Cancelled,
            "відбулося" => BookingStatuses.Completed,
            _ => null,
        };
        if (status is null)
        {
            return "unknown_status";
        }

        if (status == BookingStatuses.Active && await db.Bookings.AnyAsync(b => b.SlotId == slotId && b.Status == BookingStatuses.Active, ct).ConfigureAwait(false))
        {
            return "duplicate_active_attempt"; // програна спроба одночасного запису в n8n
        }

        var booking = new BookingEntity
        {
            SlotId = slotId,
            TelegramId = SheetValues.Long(Get(row, "telegram_id")),
            Status = status,
            CreatedAt = SheetValues.Moment(Get(row, "created_at"), Zone) ?? _startedAt,
            CancelledAt = SheetValues.Moment(Get(row, "cancelled_at"), Zone),
            CancelledBy = status == BookingStatuses.Cancelled ? CancelledBy.User : null,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RememberAsync("bookings", id, booking.Id, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> EventAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var eid = Get(row, "eid");
        if (eid.Length == 0 || Get(row, "title").Length == 0)
        {
            return "no_eid_or_title";
        }

        if (await MappedAsync("events", eid, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        if (SheetValues.Date(Get(row, "date")) is not { } date)
        {
            return "bad_date_or_time";
        }

        var e = new EventEntity
        {
            Title = Get(row, "title"),
            Description = Get(row, "description") is { Length: > 0 } d ? d : null,
            StartsAt = SheetValues.At(date, SheetValues.Time(Get(row, "time")) ?? TimeOnly.MinValue, Zone),
            Format = Get(row, "format") is { Length: > 0 } f ? f : null,
            Host = Get(row, "host") is { Length: > 0 } h ? h : null,
            Contact = Get(row, "contact") is { Length: > 0 } c ? c : null,
            Capacity = Math.Max(0, SheetValues.Int(Get(row, "capacity"), 0)),
            Status = Get(row, "status").ToLowerInvariant() switch { "відкрито" => EventStatuses.Open, "чернетка" => EventStatuses.Draft, _ => EventStatuses.Closed },
            Notice = Get(row, "notice") is { Length: > 0 } n ? n : null,
            NoticeSentAt = SheetValues.Moment(Get(row, "notice_sent_at"), Zone),
        };
        e.NoticeSentText = e.NoticeSentAt is null ? null : e.Notice;
        db.Events.Add(e);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RememberAsync("events", eid, e.Id, ct).ConfigureAwait(false);
        return null;
    }

    private static string? RegistrationStatus(string value) => value.ToLowerInvariant() switch
    {
        "активний" => RegistrationStatuses.Active,
        "скасовано" => RegistrationStatuses.Cancelled,
        "відбулося" => RegistrationStatuses.Attended,
        _ => null,
    };

    private async Task<string?> RegistrationAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var tid = SheetValues.Long(Get(row, "telegram_id"));
        var id = Get(row, "reg_id") is { Length: > 0 } r ? r : $"{Get(row, "eid")}#{Get(row, "telegram_id")}";
        if (await MappedAsync("registrations", id, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        if (await MappedAsync("events", Get(row, "eid"), ct).ConfigureAwait(false) is not { } eventId)
        {
            return "unknown_event";
        }

        if (tid is null || RegistrationStatus(Get(row, "status")) is not { } status)
        {
            return tid is null ? "no_telegram_id" : "unknown_status";
        }

        if (await db.EventRegistrations.AnyAsync(x => x.EventId == eventId && x.TelegramId == tid, ct).ConfigureAwait(false))
        {
            return "duplicate";
        }

        var reg = new EventRegistrationEntity
        {
            EventId = eventId,
            TelegramId = tid,
            Status = status,
            CreatedAt = SheetValues.Moment(Get(row, "created_at"), Zone) ?? _startedAt,
            Feedback = SheetValues.Int(Get(row, "feedback"), 0) is var fb and >= 1 and <= 5 ? fb : null,
        };
        db.EventRegistrations.Add(reg);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RememberAsync("registrations", id, reg.Id, ct).ConfigureAwait(false);
        await RemindersAsync(ReminderKinds.Event, reg.Id, row, ct).ConfigureAwait(false);
        return null;
    }

    private async Task RemindersAsync(string kind, long targetId, IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        foreach (var offset in NotificationService.Offsets)
        {
            var mark = Get(row, $"r{offset}");
            if (mark.Length == 0)
            {
                continue;
            }

            var sentAt = SheetValues.Moment(mark, Zone) ?? _startedAt;
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO reminders (kind, target_id, offset_min, sent_at) VALUES ({kind}, {targetId}, {offset}, {sentAt}) ON CONFLICT (kind, target_id, offset_min) DO NOTHING",
                ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> MaterialAsync(IReadOnlyDictionary<string, string> row, int index, CancellationToken ct)
    {
        var mid = Get(row, "mid");
        if (mid.Length == 0 || Get(row, "title").Length == 0)
        {
            return "no_mid_or_title";
        }

        if (await MappedAsync("materials", mid, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        var status = Get(row, "status").ToLowerInvariant() switch
        {
            "опубліковано" => MaterialStatuses.Published,
            "архів" => MaterialStatuses.Archived,
            _ => MaterialStatuses.Draft,
        };
        var m = new MaterialEntity
        {
            Title = Get(row, "title"),
            Body = Get(row, "body") is { Length: > 0 } b ? b : null,
            Link = Get(row, "link") is { Length: > 0 } l ? l : null,
            Status = status,

            // Порядок «нові зверху»: нижчі рядки таблиці — новіші.
            PublishedAt = status == MaterialStatuses.Draft ? null : _startedAt.AddSeconds(index),
            Views = Math.Max(0, SheetValues.Long(Get(row, "views")) ?? 0),
        };
        db.Materials.Add(m);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RememberAsync("materials", mid, m.Id, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> SessionAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var key = SlotKey(row, "psychologist");
        if (await MappedAsync("bst_sessions", key, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        if (await MappedAsync("psychologists", Get(row, "psychologist"), ct).ConfigureAwait(false) is not { } pid)
        {
            return "unknown_psychologist";
        }

        if (SheetValues.Date(Get(row, "date")) is not { } date || SheetValues.Time(Get(row, "time")) is not { } time)
        {
            return "bad_date_or_time";
        }

        var s = new BstSessionEntity
        {
            PsychologistId = pid,
            StartsAt = SheetValues.At(date, time, Zone),
            Format = Get(row, "format") is { Length: > 0 } f ? f : null,
            Capacity = Math.Max(0, SheetValues.Int(Get(row, "capacity"), await settings.GetIntAsync(SettingsService.BstCapacityDefault, ct).ConfigureAwait(false))),
            Status = Get(row, "status").ToLowerInvariant() == "закрито" ? SessionStatuses.Closed : SessionStatuses.Open,
            Notice = Get(row, "notice") is { Length: > 0 } n ? n : null,
            NoticeSentAt = SheetValues.Moment(Get(row, "notice_sent_at"), Zone),
        };
        s.NoticeSentText = s.NoticeSentAt is null ? null : s.Notice;
        db.BstSessions.Add(s);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RememberAsync("bst_sessions", key, s.Id, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> BstBookingAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var tid = SheetValues.Long(Get(row, "telegram_id"));
        var id = Get(row, "booking_id") is { Length: > 0 } b ? b : $"{Get(row, "session_key")}#{Get(row, "telegram_id")}";
        if (await MappedAsync("bst_bookings", id, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        if (await MappedAsync("bst_sessions", Get(row, "session_key"), ct).ConfigureAwait(false) is not { } sessionId)
        {
            return "unknown_session";
        }

        if (tid is null || RegistrationStatus(Get(row, "status")) is not { } status)
        {
            return tid is null ? "no_telegram_id" : "unknown_status";
        }

        if (await db.BstBookings.AnyAsync(x => x.SessionId == sessionId && x.TelegramId == tid, ct).ConfigureAwait(false))
        {
            return "duplicate";
        }

        var booking = new BstBookingEntity { SessionId = sessionId, TelegramId = tid, Status = status, CreatedAt = SheetValues.Moment(Get(row, "created_at"), Zone) ?? _startedAt };
        db.BstBookings.Add(booking);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RememberAsync("bst_bookings", id, booking.Id, ct).ConfigureAwait(false);
        await RemindersAsync(ReminderKinds.Bst, booking.Id, row, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> WaitlistAsync(IReadOnlyDictionary<string, string> row, CancellationToken ct)
    {
        var tid = SheetValues.Long(Get(row, "telegram_id"));
        var id = Get(row, "wl_id") is { Length: > 0 } w ? w : $"{Get(row, "session_key")}#{Get(row, "telegram_id")}";
        if (await MappedAsync("waitlist", id, ct).ConfigureAwait(false) is not null)
        {
            return Exists;
        }

        if (await MappedAsync("bst_sessions", Get(row, "session_key"), ct).ConfigureAwait(false) is not { } sessionId)
        {
            return "unknown_session";
        }

        if (tid is null)
        {
            return "no_telegram_id";
        }

        var notifiedAt = SheetValues.Moment(Get(row, "notified_at"), Zone);
        var status = Get(row, "status").ToLowerInvariant() switch
        {
            "активний" => notifiedAt is null ? WaitlistStatuses.Waiting : WaitlistStatuses.Offered,
            "отримав місце" => WaitlistStatuses.Booked,
            "скасовано" => WaitlistStatuses.Cancelled,
            _ => null,
        };
        if (status is null)
        {
            return "unknown_status";
        }

        if (status is WaitlistStatuses.Waiting or WaitlistStatuses.Offered
            && await db.Waitlist.AnyAsync(x => x.SessionId == sessionId && x.TelegramId == tid && (x.Status == WaitlistStatuses.Waiting || x.Status == WaitlistStatuses.Offered), ct).ConfigureAwait(false))
        {
            return "duplicate";
        }

        var entry = new WaitlistEntity { SessionId = sessionId, TelegramId = tid, Status = status, AddedAt = SheetValues.Moment(Get(row, "added_at"), Zone) ?? _startedAt, OfferedAt = notifiedAt };
        db.Waitlist.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RememberAsync("waitlist", id, entry.Id, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Слоти «зайнятий» без активного запису з листа <c>bookings</c> — запис з <c>booked_by</c>.</summary>
    internal async Task<int> BookedBySlotsAsync(CancellationToken ct)
    {
        var created = 0;
        var marks = await db.ImportMap.AsNoTracking().Where(m => m.Sheet == "slot_booked_by").ToListAsync(ct).ConfigureAwait(false);
        foreach (var mark in marks)
        {
            if (await MappedAsync("slots", mark.OldKey, ct).ConfigureAwait(false) is not { } slotId
                || await db.Bookings.AnyAsync(b => b.SlotId == slotId && b.Status == BookingStatuses.Active, ct).ConfigureAwait(false)
                || await MappedAsync("bookings_from_slots", mark.OldKey, ct).ConfigureAwait(false) is not null)
            {
                continue;
            }

            var booking = new BookingEntity { SlotId = slotId, TelegramId = mark.NewId, CreatedAt = _startedAt };
            db.Bookings.Add(booking);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await RememberAsync("bookings_from_slots", mark.OldKey, booking.Id, ct).ConfigureAwait(false);
            created++;
        }

        return created;
    }
}
