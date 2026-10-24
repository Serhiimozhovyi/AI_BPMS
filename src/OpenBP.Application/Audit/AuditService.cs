using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Paging;
using OpenBP.Data;

namespace OpenBP.Application.Audit;

/// <inheritdoc />
public sealed class AuditService(OpenBpDbContext db, ICurrentUser currentUser) : IAuditService
{
    /// <inheritdoc />
    public async Task<PageResult<AuditEntry>> ListAsync(AuditFilter filter, PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        currentUser.Require(UserRole.Admin);
        page = page.Normalized();

        var query = db.AuditLog.AsNoTracking();
        if (filter.UserId is { } userId)
        {
            query = query.Where(a => a.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            var action = filter.Action.Trim();
            query = action.EndsWith('.')
                ? query.Where(a => a.Action.StartsWith(action))
                : query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            query = query.Where(a => a.EntityType == filter.EntityType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
        {
            query = query.Where(a => a.EntityId == filter.EntityId.Trim());
        }

        if (filter.From is { } from)
        {
            query = query.Where(a => a.At >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(a => a.At < to);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .Select(a => new
            {
                Entry = a,
                Email = db.Users.Where(u => u.Id == a.UserId).Select(u => u.Email).FirstOrDefault(),
                KeyName = db.ApiKeys.Where(k => k.Id == a.ApiKeyId).Select(k => k.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var items = rows.Select(r => new AuditEntry(
            r.Entry.Id, r.Entry.At, r.Entry.UserId, r.Email, r.Entry.ApiKeyId, r.KeyName,
            r.Entry.Action, r.Entry.EntityType, r.Entry.EntityId,
            JsonNode.Parse(r.Entry.Details) as JsonObject ?? [])).ToList();
        return new PageResult<AuditEntry>(items, page.Page, page.PageSize, total);
    }
}
