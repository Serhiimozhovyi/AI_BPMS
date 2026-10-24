using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Api;

public sealed record LoginRequest(string Email, string Password);

public sealed record InviteAcceptRequest(string Token, string Password);

public sealed record MeResponse(long Id, string Email, string Role, long? PsychologistId, string Timezone);

public sealed record PsychologistRequest(string Name, string? Phone, string? Email, bool Active = true);

public sealed record StaffRequest(string Email, string Role, long? PsychologistId);

public sealed record StaffResponse(long Id, string Email, string Role, long? PsychologistId, bool Disabled, bool PasswordSet, DateTimeOffset? LastLoginAt);

public sealed record InviteResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record SettingRequest(string Value);

public sealed record SlotRequest(long PsychologistId, DateTimeOffset StartsAt, int? DurationMin, string? Format);

public sealed record SlotStatusRequest(string Status);

public sealed record SlotResponse(long Id, long PsychologistId, string PsychologistName, DateTimeOffset StartsAt, int DurationMin, string Format, string Status, string Source, long? BookingId);

public sealed record TemplateRequest(long PsychologistId, int Weekday, TimeOnly Time, int? DurationMin, string? Format, DateOnly? ValidFrom, DateOnly? ValidTo);

public sealed record GenerateRequest(int Days);

/// <summary>Запис у розкладі адмінки; ім'я й телефон людини — лише у своїх записах психолога і координатору (§7.1, §8).</summary>
public sealed record AdminBookingResponse(
    long Id, long SlotId, long PsychologistId, string PsychologistName, DateTimeOffset StartsAt, int DurationMin, string Format, string Status, string? PersonName, string? Phone);

public sealed record EventRequest(string Title, string? Description, DateTimeOffset StartsAt, string? Format, string? Host, string? Contact, int Capacity, string Status, string? Notice);

public sealed record BstRequest(long PsychologistId, DateTimeOffset StartsAt, string? Format, int? Capacity, string Status, string? Notice);

public sealed record ParticipantResponse(long Id, string? Name, string? Phone, string Status, DateTimeOffset CreatedAt, int? Feedback);

public sealed record BstParticipantsResponse(IReadOnlyList<ParticipantResponse> Participants, IReadOnlyList<ParticipantResponse> Waitlist);

public sealed record MaterialRequest(string Title, string? Body, string? Link, string Status);

public sealed record UserSearchResponse(long TelegramId, string? Name, string? Phone, DateTimeOffset RegisteredAt);

public sealed record NoticeResponse(string Code, int Queued);

public sealed record ErrorResponse(string Code, string? Message = null);

/// <summary>
/// REST <c>/api/v1</c> для адмінки (docs/psy-support.md §7.1). Cookie-сесія, ролі <c>coordinator</c> / <c>psychologist</c>.
/// Сповіщення від дій тут ідуть в <c>outbox</c> (§1).
/// </summary>
public static class AdminApi
{
    public const string CoordinatorPolicy = "coordinator";

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        MapAuth(api);
        var staff = api.MapGroup("").RequireAuthorization();
        var coordinator = api.MapGroup("").RequireAuthorization(CoordinatorPolicy);
        MapPeople(staff, coordinator);
        MapSchedule(staff, coordinator);
        MapEvents(staff, coordinator);
        MapBst(staff);
        MapMaterials(staff, coordinator);
        MapAdmin(coordinator);
    }

    private static void MapAuth(RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", async (LoginRequest req, StaffService staff, HttpContext http, CancellationToken ct) =>
        {
            var (outcome, account) = await staff.LoginAsync(req.Email ?? "", req.Password ?? "", ct).ConfigureAwait(false);
            if (outcome == LoginOutcome.Locked)
            {
                return Results.Json(new ErrorResponse("locked", "Забагато невдалих спроб. Спробуйте за 15 хвилин."), statusCode: StatusCodes.Status423Locked);
            }

            if (account is null)
            {
                return Results.Json(new ErrorResponse("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);
            }

            await SignInAsync(http, account).ConfigureAwait(false);
            return Results.Ok(Me(account, http));
        });

        api.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return Results.NoContent();
        });

        api.MapGet("/auth/me", async (ClaimsPrincipal user, PsyDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var account = await db.StaffAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == me.Id && a.DisabledAt == null, ct).ConfigureAwait(false);
            return account is null ? Results.Unauthorized() : Results.Ok(Me(account, http));
        }).RequireAuthorization();

        api.MapPost("/auth/invite", async (InviteAcceptRequest req, StaffService staff, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var account = await staff.AcceptInviteAsync(req.Token ?? "", req.Password ?? "", ct).ConfigureAwait(false);
                if (account is null)
                {
                    return Results.Json(new ErrorResponse("invite_invalid", "Посилання недійсне або прострочене."), statusCode: StatusCodes.Status400BadRequest);
                }

                await SignInAsync(http, account).ConfigureAwait(false);
                return Results.Ok(Me(account, http));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse("weak_password", ex.Message));
            }
        });
    }

    private static MeResponse Me(StaffAccountEntity a, HttpContext http) =>
        new(a.Id, a.Email, a.Role, a.PsychologistId, http.RequestServices.GetRequiredService<PsyOptions>().Timezone);

    private static Task SignInAsync(HttpContext http, StaffAccountEntity account)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ClaimTypes.Role, account.Role),
        };
        if (account.PsychologistId is { } pid)
        {
            claims.Add(new Claim(StaffUser.PsychologistClaim, pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }

    // ---------- Психологи, облікові записи, люди ----------

    private static void MapPeople(RouteGroupBuilder staff, RouteGroupBuilder coordinator)
    {
        staff.MapGet("/psychologists", async (ClaimsPrincipal user, PsyDbContext db, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var list = await db.Psychologists.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct).ConfigureAwait(false);
            return list.Select(p => new { p.Id, p.Name, p.Email, Phone = me.IsCoordinator ? p.Phone : null, p.Active, LinkedToTelegram = p.TelegramId != null });
        });

        coordinator.MapPost("/psychologists", async (PsychologistRequest req, ClaimsPrincipal user, PsyDbContext db, PsyClock clock, AuditService audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.BadRequest(new ErrorResponse("name_required"));
            }

            var p = new PsychologistEntity { Name = req.Name.Trim(), Phone = UserService.NormalizePhone(req.Phone), Email = req.Email?.Trim(), Active = req.Active, CreatedAt = clock.Now };
            db.Psychologists.Add(p);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            audit.Add(StaffUser.From(user).Actor, "psychologist.created", "psychologist", p.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { p.Id });
        });

        coordinator.MapPut("/psychologists/{id:long}", async (long id, PsychologistRequest req, ClaimsPrincipal user, PsyDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var p = await db.Psychologists.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            if (p is null)
            {
                return Results.NotFound();
            }

            var phone = UserService.NormalizePhone(req.Phone);
            if (phone != p.Phone)
            {
                p.TelegramId = null; // прив'язка була за старим номером
            }

            p.Name = req.Name?.Trim() is { Length: > 0 } name ? name : p.Name;
            p.Phone = phone;
            p.Email = req.Email?.Trim();
            p.Active = req.Active;
            audit.Add(StaffUser.From(user).Actor, "psychologist.updated", "psychologist", p.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        coordinator.MapGet("/staff", async (PsyDbContext db, CancellationToken ct) =>
            (await db.StaffAccounts.AsNoTracking().OrderBy(a => a.Email).ToListAsync(ct).ConfigureAwait(false))
                .Select(a => new StaffResponse(a.Id, a.Email, a.Role, a.PsychologistId, a.DisabledAt != null, a.PasswordHash != null, a.LastLoginAt)));

        coordinator.MapPost("/staff", async (StaffRequest req, ClaimsPrincipal user, PsyDbContext db, PsyClock clock, StaffService staffService, CancellationToken ct) =>
        {
            if (req.Role is not (StaffRoles.Coordinator or StaffRoles.Psychologist) || string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@', StringComparison.Ordinal))
            {
                return Results.BadRequest(new ErrorResponse("invalid"));
            }

            if (req.Role == StaffRoles.Psychologist && (req.PsychologistId is not { } pid || !await db.Psychologists.AnyAsync(p => p.Id == pid, ct).ConfigureAwait(false)))
            {
                return Results.BadRequest(new ErrorResponse("psychologist_required"));
            }

            var email = StaffService.NormalizeEmail(req.Email);
            if (await db.StaffAccounts.AnyAsync(a => a.Email == email, ct).ConfigureAwait(false))
            {
                return Results.Conflict(new ErrorResponse("email_taken"));
            }

            var account = new StaffAccountEntity { Email = email, Role = req.Role, PsychologistId = req.Role == StaffRoles.Psychologist ? req.PsychologistId : null, CreatedAt = clock.Now };
            db.StaffAccounts.Add(account);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var token = await staffService.CreateInviteAsync(account.Id, StaffUser.From(user).Actor, ct).ConfigureAwait(false);
            return Results.Ok(new { account.Id, Invite = new InviteResponse(token, clock.Now + StaffService.InviteLifetime) });
        });

        coordinator.MapPost("/staff/{id:long}/invite", async (long id, ClaimsPrincipal user, PsyDbContext db, PsyClock clock, StaffService staffService, CancellationToken ct) =>
        {
            if (!await db.StaffAccounts.AnyAsync(a => a.Id == id, ct).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            var token = await staffService.CreateInviteAsync(id, StaffUser.From(user).Actor, ct).ConfigureAwait(false);
            return Results.Ok(new InviteResponse(token, clock.Now + StaffService.InviteLifetime));
        });

        coordinator.MapPost("/staff/{id:long}/disabled", async (long id, bool disabled, ClaimsPrincipal user, PsyDbContext db, PsyClock clock, AuditService audit, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var account = await db.StaffAccounts.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
            if (account is null)
            {
                return Results.NotFound();
            }

            if (account.Id == me.Id)
            {
                return Results.BadRequest(new ErrorResponse("cannot_disable_self"));
            }

            account.DisabledAt = disabled ? clock.Now : null;
            audit.Add(me.Actor, disabled ? "staff.disabled" : "staff.enabled", "staff_account", id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        coordinator.MapGet("/users", async (string q, PsyDbContext db, ClaimsPrincipal user, AuditService audit, CancellationToken ct) =>
        {
            var phone = UserService.NormalizePhone(q);
            var term = q.Trim();
            var list = await db.Users.AsNoTracking()
                .Where(u => u.DeletedAt == null && (phone != null ? u.Phone == phone : u.Name != null && EF.Functions.ILike(u.Name, "%" + term + "%")))
                .OrderBy(u => u.Name).Take(20).ToListAsync(ct).ConfigureAwait(false);
            audit.Add(StaffUser.From(user).Actor, "users.searched", null, null, new { results = list.Count });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return list.Select(u => new UserSearchResponse(u.TelegramId, u.Name, u.Phone, u.RegisteredAt));
        });

        coordinator.MapPost("/users/{telegramId:long}/delete", async (long telegramId, ClaimsPrincipal user, MaintenanceService maintenance, CancellationToken ct) =>
        {
            var result = await maintenance.DeleteUserAsync(telegramId, StaffUser.From(user).Actor, ct).ConfigureAwait(false);
            return result.Ok ? Results.NoContent() : Results.NotFound();
        });
    }

    // ---------- Розклад і записи ----------

    private static bool CanManage(StaffUser me, long psychologistId) => me.IsCoordinator || me.PsychologistId == psychologistId;

    private static void MapSchedule(RouteGroupBuilder staff, RouteGroupBuilder coordinator)
    {
        staff.MapGet("/slots", async (DateTimeOffset from, DateTimeOffset to, long? psychologistId, ClaimsPrincipal user, PsyDbContext db, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var (fromAt, toAt) = (from, to);
            var pid = me.IsCoordinator ? psychologistId : me.PsychologistId ?? -1;
            var query = from s in db.Slots.AsNoTracking()
                        join p in db.Psychologists on s.PsychologistId equals p.Id
                        where s.StartsAt >= fromAt && s.StartsAt <= toAt && (pid == null || s.PsychologistId == pid)
                        orderby s.StartsAt
                        select new SlotResponse(s.Id, p.Id, p.Name, s.StartsAt, s.DurationMin, s.Format, s.Status, s.Source,
                            db.Bookings.Where(b => b.SlotId == s.Id && b.Status == BookingStatuses.Active).Select(b => (long?)b.Id).FirstOrDefault());
            return await query.ToListAsync(ct).ConfigureAwait(false);
        });

        staff.MapPost("/slots", async (SlotRequest req, ClaimsPrincipal user, PsyDbContext db, PsyClock clock, SettingsService settings, AuditService audit, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            if (!CanManage(me, req.PsychologistId))
            {
                return Results.Forbid();
            }

            if (req.StartsAt <= clock.Now)
            {
                return Results.BadRequest(new ErrorResponse("in_past"));
            }

            var slot = new SlotEntity
            {
                PsychologistId = req.PsychologistId,
                StartsAt = req.StartsAt.ToUniversalTime(),
                DurationMin = req.DurationMin ?? await settings.GetIntAsync(SettingsService.SlotDurationMin, ct).ConfigureAwait(false),
                Format = string.IsNullOrWhiteSpace(req.Format) ? "онлайн" : req.Format.Trim(),
                CreatedAt = clock.Now,
            };
            db.Slots.Add(slot);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (PgErrors.IsUniqueViolation(ex))
            {
                return Results.Conflict(new ErrorResponse("slot_exists"));
            }

            audit.Add(me.Actor, "slot.created", "slot", slot.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { slot.Id });
        });

        staff.MapPut("/slots/{id:long}/status", async (long id, SlotStatusRequest req, ClaimsPrincipal user, PsyDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var slot = await db.Slots.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (slot is null)
            {
                return Results.NotFound();
            }

            if (!CanManage(me, slot.PsychologistId))
            {
                return Results.Forbid();
            }

            if (req.Status is not (SlotStatuses.Open or SlotStatuses.Closed))
            {
                return Results.BadRequest(new ErrorResponse("invalid_status"));
            }

            slot.Status = req.Status;
            audit.Add(me.Actor, "slot.status", "slot", id, new { status = req.Status });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        staff.MapDelete("/slots/{id:long}", async (long id, ClaimsPrincipal user, PsyDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var slot = await db.Slots.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (slot is null)
            {
                return Results.NotFound();
            }

            if (!CanManage(me, slot.PsychologistId))
            {
                return Results.Forbid();
            }

            if (await db.Bookings.AnyAsync(b => b.SlotId == id, ct).ConfigureAwait(false))
            {
                return Results.Conflict(new ErrorResponse("slot_has_bookings", "Слот має записи: закрийте його замість видалення."));
            }

            db.Slots.Remove(slot);
            audit.Add(me.Actor, "slot.deleted", "slot", id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        staff.MapGet("/slot-templates", async (long? psychologistId, ClaimsPrincipal user, PsyDbContext db, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var pid = me.IsCoordinator ? psychologistId : me.PsychologistId ?? -1;
            return await db.SlotTemplates.AsNoTracking().Where(t => pid == null || t.PsychologistId == pid)
                .OrderBy(t => t.PsychologistId).ThenBy(t => t.Weekday).ThenBy(t => t.Time).ToListAsync(ct).ConfigureAwait(false);
        });

        staff.MapPost("/slot-templates", async (TemplateRequest req, ClaimsPrincipal user, PsyDbContext db, SettingsService settings, AuditService audit, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            if (!CanManage(me, req.PsychologistId))
            {
                return Results.Forbid();
            }

            if (req.Weekday is < 1 or > 7)
            {
                return Results.BadRequest(new ErrorResponse("invalid_weekday"));
            }

            var t = new SlotTemplateEntity
            {
                PsychologistId = req.PsychologistId,
                Weekday = req.Weekday,
                Time = req.Time,
                DurationMin = req.DurationMin ?? await settings.GetIntAsync(SettingsService.SlotDurationMin, ct).ConfigureAwait(false),
                Format = string.IsNullOrWhiteSpace(req.Format) ? "онлайн" : req.Format.Trim(),
                ValidFrom = req.ValidFrom,
                ValidTo = req.ValidTo,
            };
            db.SlotTemplates.Add(t);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            audit.Add(me.Actor, "slot_template.created", "slot_template", t.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { t.Id });
        });

        staff.MapDelete("/slot-templates/{id:long}", async (long id, ClaimsPrincipal user, PsyDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var t = await db.SlotTemplates.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            if (t is null)
            {
                return Results.NotFound();
            }

            if (!CanManage(me, t.PsychologistId))
            {
                return Results.Forbid();
            }

            db.SlotTemplates.Remove(t);
            audit.Add(me.Actor, "slot_template.deleted", "slot_template", id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        coordinator.MapPost("/slots/generate", async (GenerateRequest req, MaintenanceService maintenance, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(new { Created = await maintenance.GenerateSlotsAsync(req.Days, ct).ConfigureAwait(false) });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new ErrorResponse("invalid_days", ex.Message));
            }
        });

        staff.MapGet("/bookings", async (DateTimeOffset from, DateTimeOffset to, ClaimsPrincipal user, ConsultService consult, UserService users, SettingsService settings, PsyClock clock, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var list = await consult.ListAsync(from, to, null, ct).ConfigureAwait(false);
            if (!me.IsCoordinator)
            {
                // Записи колег — лише на PSY_PEER_VIEW_DAYS днів уперед і без даних людини.
                var peerUntil = clock.Now.AddDays(await settings.GetIntAsync(SettingsService.PeerViewDays, ct).ConfigureAwait(false));
                list = [.. list.Where(b => b.PsychologistId == me.PsychologistId || b.StartsAt <= peerUntil)];
            }

            var visible = list.Where(b => me.IsCoordinator || b.PsychologistId == me.PsychologistId).Where(b => b.TelegramId is not null).Select(b => b.TelegramId!.Value);
            var people = await users.ByIdsAsync(visible, ct).ConfigureAwait(false);
            return list.Select(b =>
            {
                var person = (me.IsCoordinator || b.PsychologistId == me.PsychologistId) && b.TelegramId is { } t && people.TryGetValue(t, out var u) ? u : null;
                return new AdminBookingResponse(b.Id, b.SlotId, b.PsychologistId, b.PsychologistName, b.StartsAt, b.DurationMin, b.Format, b.Status, person?.Name, person?.Phone);
            });
        });

        staff.MapPost("/bookings/{id:long}/cancel", async (long id, ClaimsPrincipal user, ConsultService consult, NotificationService notifications, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var booking = await consult.BookingAsync(id, ct).ConfigureAwait(false);
            if (booking is null)
            {
                return Results.NotFound();
            }

            if (!CanManage(me, booking.PsychologistId))
            {
                return Results.Forbid();
            }

            var result = await consult.CancelAsync(null, id, me.IsCoordinator ? CancelledBy.Coordinator : CancelledBy.Psychologist, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Results.Conflict(new ErrorResponse(result.Code));
            }

            await notifications.EnqueueAsync(result.Notifications ?? [], ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    // ---------- Події ----------

    private static void MapEvents(RouteGroupBuilder staff, RouteGroupBuilder coordinator)
    {
        staff.MapGet("/events", async (DateTimeOffset? from, EventService events, CancellationToken ct) =>
            await events.ListAsync(from, ct).ConfigureAwait(false));

        coordinator.MapPost("/events", async (EventRequest req, ClaimsPrincipal user, PsyDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (Validate(req) is { } error)
            {
                return error;
            }

            var e = new EventEntity { Title = req.Title.Trim() };
            Apply(e, req);
            db.Events.Add(e);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            audit.Add(StaffUser.From(user).Actor, "event.created", "event", e.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { e.Id });
        });

        coordinator.MapPut("/events/{id:long}", async (long id, EventRequest req, ClaimsPrincipal user, PsyDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (Validate(req) is { } error)
            {
                return error;
            }

            var e = await db.Events.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            if (e is null)
            {
                return Results.NotFound();
            }

            Apply(e, req);
            audit.Add(StaffUser.From(user).Actor, "event.updated", "event", id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        staff.MapGet("/events/{id:long}/registrations", async (long id, ClaimsPrincipal user, PsyDbContext db, UserService users, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var rows = await db.EventRegistrations.AsNoTracking().Where(r => r.EventId == id).OrderBy(r => r.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
            var people = await users.ByIdsAsync(rows.Where(r => r.TelegramId is not null).Select(r => r.TelegramId!.Value), ct).ConfigureAwait(false);
            return rows.Select(r =>
            {
                var u = r.TelegramId is { } t && people.TryGetValue(t, out var x) ? x : null;
                return new ParticipantResponse(r.Id, u?.Name, me.IsCoordinator ? u?.Phone : null, r.Status, r.CreatedAt, r.Feedback);
            });
        });

        staff.MapPost("/events/{id:long}/notice", async (long id, ClaimsPrincipal user, EventService events, NotificationService notifications, CancellationToken ct) =>
        {
            var result = await events.SendNoticeAsync(id, StaffUser.From(user).Actor, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                return result.Code == Codes.NotFound ? Results.NotFound() : Results.Conflict(new ErrorResponse(result.Code));
            }

            await notifications.EnqueueAsync(result.Notifications ?? [], ct).ConfigureAwait(false);
            return Results.Ok(new NoticeResponse(result.Code, result.Notifications?.Count ?? 0));
        });
    }

    private static IResult? Validate(EventRequest req) =>
        string.IsNullOrWhiteSpace(req.Title) ? Results.BadRequest(new ErrorResponse("title_required"))
        : req.Capacity < 0 ? Results.BadRequest(new ErrorResponse("invalid_capacity"))
        : req.Status is not (EventStatuses.Draft or EventStatuses.Open or EventStatuses.Closed) ? Results.BadRequest(new ErrorResponse("invalid_status"))
        : null;

    private static void Apply(EventEntity e, EventRequest req)
    {
        e.Title = req.Title.Trim();
        e.Description = req.Description;
        e.StartsAt = req.StartsAt.ToUniversalTime();
        e.Format = req.Format;
        e.Host = req.Host;
        e.Contact = req.Contact;
        e.Capacity = req.Capacity;
        e.Status = req.Status;
        e.Notice = req.Notice;
    }

    // ---------- БСТ ----------

    private static void MapBst(RouteGroupBuilder staff)
    {
        staff.MapGet("/bst", async (DateTimeOffset? from, ClaimsPrincipal user, BstService bst, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            return await bst.ListAsync(from, me.IsCoordinator ? null : me.PsychologistId ?? -1, ct).ConfigureAwait(false);
        });

        staff.MapPost("/bst", async (BstRequest req, ClaimsPrincipal user, PsyDbContext db, SettingsService settings, AuditService audit, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            if (!CanManage(me, req.PsychologistId))
            {
                return Results.Forbid();
            }

            if (req.Status is not (SessionStatuses.Open or SessionStatuses.Closed) || req.Capacity < 0)
            {
                return Results.BadRequest(new ErrorResponse("invalid"));
            }

            var s = new BstSessionEntity
            {
                PsychologistId = req.PsychologistId,
                StartsAt = req.StartsAt.ToUniversalTime(),
                Format = req.Format,
                Capacity = req.Capacity ?? await settings.GetIntAsync(SettingsService.BstCapacityDefault, ct).ConfigureAwait(false),
                Status = req.Status,
                Notice = req.Notice,
            };
            db.BstSessions.Add(s);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            audit.Add(me.Actor, "bst.created", "bst_session", s.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { s.Id });
        });

        staff.MapPut("/bst/{id:long}", async (long id, BstRequest req, ClaimsPrincipal user, PsyDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var s = await db.BstSessions.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            if (s is null)
            {
                return Results.NotFound();
            }

            if (!CanManage(me, s.PsychologistId) || !CanManage(me, req.PsychologistId))
            {
                return Results.Forbid();
            }

            if (req.Status is not (SessionStatuses.Open or SessionStatuses.Closed) || req.Capacity < 0)
            {
                return Results.BadRequest(new ErrorResponse("invalid"));
            }

            s.PsychologistId = req.PsychologistId;
            s.StartsAt = req.StartsAt.ToUniversalTime();
            s.Format = req.Format;
            s.Capacity = req.Capacity ?? s.Capacity;
            s.Status = req.Status;
            s.Notice = req.Notice;
            audit.Add(me.Actor, "bst.updated", "bst_session", id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        staff.MapGet("/bst/{id:long}/participants", async (long id, ClaimsPrincipal user, PsyDbContext db, UserService users, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var session = await db.BstSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (session is null)
            {
                return Results.NotFound();
            }

            if (!CanManage(me, session.PsychologistId))
            {
                return Results.Forbid();
            }

            var bookings = await db.BstBookings.AsNoTracking().Where(b => b.SessionId == id).OrderBy(b => b.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
            var waitlist = await db.Waitlist.AsNoTracking().Where(w => w.SessionId == id && (w.Status == WaitlistStatuses.Waiting || w.Status == WaitlistStatuses.Offered))
                .OrderBy(w => w.AddedAt).ToListAsync(ct).ConfigureAwait(false);
            var people = await users.ByIdsAsync(
                bookings.Select(b => b.TelegramId).Concat(waitlist.Select(w => w.TelegramId)).Where(t => t is not null).Select(t => t!.Value), ct).ConfigureAwait(false);
            UserEntity? Person(long? t) => t is { } v && people.TryGetValue(v, out var u) ? u : null;
            return Results.Ok(new BstParticipantsResponse(
                [.. bookings.Select(b => new ParticipantResponse(b.Id, Person(b.TelegramId)?.Name, me.IsCoordinator ? Person(b.TelegramId)?.Phone : null, b.Status, b.CreatedAt, null))],
                [.. waitlist.Select(w => new ParticipantResponse(w.Id, Person(w.TelegramId)?.Name, me.IsCoordinator ? Person(w.TelegramId)?.Phone : null, w.Status, w.AddedAt, null))]));
        });

        staff.MapPost("/bst/{id:long}/notice", async (long id, ClaimsPrincipal user, PsyDbContext db, BstService bst, NotificationService notifications, CancellationToken ct) =>
        {
            var me = StaffUser.From(user);
            var session = await db.BstSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (session is null)
            {
                return Results.NotFound();
            }

            if (!CanManage(me, session.PsychologistId))
            {
                return Results.Forbid();
            }

            var result = await bst.SendNoticeAsync(id, me.Actor, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Results.Conflict(new ErrorResponse(result.Code));
            }

            await notifications.EnqueueAsync(result.Notifications ?? [], ct).ConfigureAwait(false);
            return Results.Ok(new NoticeResponse(result.Code, result.Notifications?.Count ?? 0));
        });
    }

    // ---------- Матеріали ----------

    private static void MapMaterials(RouteGroupBuilder staff, RouteGroupBuilder coordinator)
    {
        staff.MapGet("/materials", async (PsyDbContext db, CancellationToken ct) =>
            await db.Materials.AsNoTracking().OrderByDescending(m => m.PublishedAt).ThenByDescending(m => m.Id).ToListAsync(ct).ConfigureAwait(false));

        coordinator.MapPost("/materials", async (MaterialRequest req, ClaimsPrincipal user, PsyDbContext db, PsyClock clock, AuditService audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title) || req.Status is not (MaterialStatuses.Draft or MaterialStatuses.Published or MaterialStatuses.Archived))
            {
                return Results.BadRequest(new ErrorResponse("invalid"));
            }

            var m = new MaterialEntity { Title = req.Title.Trim(), Body = req.Body, Link = req.Link, Status = req.Status, PublishedAt = req.Status == MaterialStatuses.Draft ? null : clock.Now };
            db.Materials.Add(m);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            audit.Add(StaffUser.From(user).Actor, "material.created", "material", m.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { m.Id });
        });

        coordinator.MapPut("/materials/{id:long}", async (long id, MaterialRequest req, ClaimsPrincipal user, PsyDbContext db, PsyClock clock, AuditService audit, CancellationToken ct) =>
        {
            var m = await db.Materials.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            if (m is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(req.Title) || req.Status is not (MaterialStatuses.Draft or MaterialStatuses.Published or MaterialStatuses.Archived))
            {
                return Results.BadRequest(new ErrorResponse("invalid"));
            }

            m.Title = req.Title.Trim();
            m.Body = req.Body;
            m.Link = req.Link;
            if (m.Status == MaterialStatuses.Draft && req.Status == MaterialStatuses.Published)
            {
                m.PublishedAt = clock.Now;
            }

            m.Status = req.Status;
            audit.Add(StaffUser.From(user).Actor, "material.updated", "material", id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    // ---------- Тексти, лічильники, журнал ----------

    private static void MapAdmin(RouteGroupBuilder coordinator)
    {
        coordinator.MapGet("/settings", async (SettingsService settings, CancellationToken ct) => await settings.AllAsync(ct).ConfigureAwait(false));

        coordinator.MapPut("/settings/{key}", async (string key, SettingRequest req, ClaimsPrincipal user, PsyDbContext db, PsyClock clock, AuditService audit, CancellationToken ct) =>
        {
            if (!SettingsService.Defaults.ContainsKey(key))
            {
                return Results.BadRequest(new ErrorResponse("unknown_key"));
            }

            if (!key.StartsWith("TEXT_", StringComparison.Ordinal) && !int.TryParse(req.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return Results.BadRequest(new ErrorResponse("number_required"));
            }

            var me = StaffUser.From(user);
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
            if (row is null)
            {
                db.Settings.Add(row = new SettingEntity { Key = key, Value = req.Value });
            }

            row.Value = req.Value;
            row.UpdatedAt = clock.Now;
            row.UpdatedBy = me.Actor;
            audit.Add(me.Actor, "setting.updated", "setting", null, new { key });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        coordinator.MapGet("/counters", async (PsyDbContext db, CancellationToken ct) =>
            await db.Counters.AsNoTracking().OrderBy(c => c.Metric).ToListAsync(ct).ConfigureAwait(false));

        coordinator.MapGet("/audit", async (int? limit, long? before, PsyDbContext db, CancellationToken ct) =>
            await db.Audit.AsNoTracking().Where(a => before == null || a.Id < before).OrderByDescending(a => a.Id)
                .Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct).ConfigureAwait(false));
    }
}
