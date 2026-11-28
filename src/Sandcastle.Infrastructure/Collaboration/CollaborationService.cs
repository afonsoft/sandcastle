using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Collaboration;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Collaboration;

/// <summary>SPEC-186: org-scoped collaboration workspace service.</summary>
public sealed class CollaborationService(AppDbContext db, IAuditService? audit = null) : ICollaborationService
{
    private static string U(Guid id) => id.ToString();

    private async Task<bool> CanAccessOrgAsync(Guid? orgId, Guid userId, CancellationToken ct) =>
        orgId is null || await db.OrgMembers.AsNoTracking()
            .AnyAsync(m => m.OrgId == orgId && m.UserId == userId, ct);

    private async Task<bool> CanAccessChannelAsync(ChannelRecord c, Guid userId, CancellationToken ct) =>
        c.OwnerUserId == U(userId) || await CanAccessOrgAsync(c.OrgId, userId, ct);

    private static ChannelInfo MapChannel(ChannelRecord c, int unread, Guid userId) =>
        new(c.Id, c.OrgId, c.OwnerUserId, c.Name, c.Type,
            JsonSerializer.Deserialize<List<string>>(c.PinnedUserIdsJson)?.Contains(U(userId)) == true,
            unread, c.CreatedAt, c.UpdatedAt);

    public async Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync(Guid userId, CancellationToken ct)
    {
        var orgs = await db.OrgMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Select(m => m.OrgId).ToListAsync(ct);
        var uid = U(userId);
        var channels = await db.CollabChannels.AsNoTracking()
            .Where(c => c.OwnerUserId == uid || (c.OrgId != null && orgs.Contains(c.OrgId.Value)))
            .ToListAsync(ct);
        channels = [.. channels.OrderByDescending(c => c.UpdatedAt)];
        var result = new List<ChannelInfo>();
        foreach (var c in channels)
        {
            var read = await db.CollabChannelReads.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ChannelId == c.Id && r.UserId == userId, ct);
            var since = read?.LastReadAt ?? DateTimeOffset.MinValue;
            var unread = (await db.CollabChannelMessages.AsNoTracking()
                .Where(m => m.ChannelId == c.Id)
                .Select(m => new { m.CreatedAt, m.SenderUserId }).ToListAsync(ct))
                .Count(m => m.CreatedAt > since && m.SenderUserId != uid);
            result.Add(MapChannel(c, unread, userId));
        }
        return result;
    }

    public async Task<ChannelInfo> CreateChannelAsync(ChannelUpsert request, Guid userId, CancellationToken ct)
    {
        if (!await CanAccessOrgAsync(request.OrgId, userId, ct)) throw new UnauthorizedAccessException();
        var now = DateTimeOffset.UtcNow;
        var row = new ChannelRecord
        {
            Id = Guid.NewGuid(),
            OrgId = request.OrgId,
            OwnerUserId = U(userId),
            Name = request.Name.Trim(),
            Type = request.Type is "dm" or "group" ? request.Type : "group",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CollabChannels.Add(row);
        await db.SaveChangesAsync(ct);
        return MapChannel(row, 0, userId);
    }

    public async Task<IReadOnlyList<ChannelMessageInfo>> ListMessagesAsync(Guid channelId, Guid userId, CancellationToken ct)
    {
        var c = await db.CollabChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == channelId, ct);
        if (c is null || !await CanAccessChannelAsync(c, userId, ct)) return [];
        var messages = await db.CollabChannelMessages.AsNoTracking()
            .Where(m => m.ChannelId == channelId).ToListAsync(ct);
        return messages.OrderBy(m => m.CreatedAt)
            .Select(m => new ChannelMessageInfo(m.Id, m.ChannelId, m.SenderUserId, m.Body, m.ParentId,
                m.ConversationId, m.ReactionsJson, m.CreatedAt)).ToList();
    }

    public async Task<ChannelMessageInfo?> PostMessageAsync(Guid channelId, ChannelMessageUpsert request, Guid userId, CancellationToken ct)
    {
        var c = await db.CollabChannels.FirstOrDefaultAsync(x => x.Id == channelId, ct);
        if (c is null || !await CanAccessChannelAsync(c, userId, ct)) return null;
        var now = DateTimeOffset.UtcNow;
        var row = new ChannelMessageRecord
        {
            Id = Guid.NewGuid(),
            ChannelId = channelId,
            SenderUserId = U(userId),
            Body = request.Body,
            ParentId = request.ParentId,
            ConversationId = request.ConversationId,
            CreatedAt = now,
        };
        db.CollabChannelMessages.Add(row);
        c.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new ChannelMessageInfo(row.Id, row.ChannelId, row.SenderUserId, row.Body,
            row.ParentId, row.ConversationId, row.ReactionsJson, row.CreatedAt);
    }

    public async Task<bool> ReactAsync(Guid messageId, string emoji, Guid userId, CancellationToken ct)
    {
        var row = await db.CollabChannelMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (row is null) return false;
        var channel = await db.CollabChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == row.ChannelId, ct);
        if (channel is null || !await CanAccessChannelAsync(channel, userId, ct)) return false;
        var map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(row.ReactionsJson) ?? [];
        var users = map.GetValueOrDefault(emoji, []);
        var uid = U(userId);
        if (!users.Remove(uid)) users.Add(uid);
        map[emoji] = users.Distinct().ToList();
        row.ReactionsJson = JsonSerializer.Serialize(map);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> MarkChannelReadAsync(Guid channelId, Guid userId, CancellationToken ct)
    {
        var c = await db.CollabChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == channelId, ct);
        if (c is null || !await CanAccessChannelAsync(c, userId, ct)) return false;
        var row = await db.CollabChannelReads.FirstOrDefaultAsync(r => r.ChannelId == channelId && r.UserId == userId, ct);
        if (row is null) db.CollabChannelReads.Add(new ChannelReadRecord { ChannelId = channelId, UserId = userId, LastReadAt = DateTimeOffset.UtcNow });
        else row.LastReadAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> PinChannelAsync(Guid channelId, bool pinned, Guid userId, CancellationToken ct)
    {
        var c = await db.CollabChannels.FirstOrDefaultAsync(x => x.Id == channelId, ct);
        if (c is null || !await CanAccessChannelAsync(c, userId, ct)) return false;
        var users = JsonSerializer.Deserialize<List<string>>(c.PinnedUserIdsJson) ?? [];
        var uid = U(userId);
        if (pinned && !users.Contains(uid)) users.Add(uid);
        if (!pinned) users.Remove(uid);
        c.PinnedUserIdsJson = JsonSerializer.Serialize(users.Distinct());
        await db.SaveChangesAsync(ct);
        if (audit is not null) await audit.RecordAsync("collab_channel_pin", uid, c.OrgId, "channel", channelId.ToString(), metadata: new { pinned }, cancellationToken: ct);
        return true;
    }

    public async Task<IReadOnlyList<NoteInfo>> ListNotesAsync(Guid userId, CancellationToken ct)
    {
        var orgs = await db.OrgMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.OrgId).ToListAsync(ct);
        var uid = U(userId);
        var notes = await db.CollabNotes.AsNoTracking()
            .Where(n => n.OwnerUserId == uid || (n.OrgId != null && orgs.Contains(n.OrgId.Value)))
            .ToListAsync(ct);
        return notes.OrderByDescending(n => n.UpdatedAt)
            .Select(n => new NoteInfo(n.Id, n.OrgId, n.OwnerUserId, n.Title, n.Body, n.Version,
                n.ConversationId, n.KnowledgeLink, n.CreatedAt, n.UpdatedAt)).ToList();
    }

    public async Task<NoteInfo> UpsertNoteAsync(Guid? id, NoteUpsert request, Guid userId, CancellationToken ct)
    {
        if (!await CanAccessOrgAsync(request.OrgId, userId, ct)) throw new UnauthorizedAccessException();
        var now = DateTimeOffset.UtcNow;
        NoteRecord row;
        if (id is null)
        {
            row = new NoteRecord
            {
                Id = Guid.NewGuid(),
                OrgId = request.OrgId,
                OwnerUserId = U(userId),
                Title = request.Title,
                Body = request.Body,
                Version = 1,
                ConversationId = request.ConversationId,
                KnowledgeLink = request.KnowledgeLink,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.CollabNotes.Add(row);
        }
        else
        {
            row = await db.CollabNotes.FirstOrDefaultAsync(n => n.Id == id, ct)
                ?? throw new KeyNotFoundException();
            if (!await CanAccessOrgAsync(row.OrgId, userId, ct) && row.OwnerUserId != U(userId)) throw new UnauthorizedAccessException();
            row.Title = request.Title; row.Body = request.Body; row.Version++;
            row.ConversationId = request.ConversationId; row.KnowledgeLink = request.KnowledgeLink;
            row.UpdatedAt = now;
        }
        db.CollabNoteVersions.Add(new NoteVersionRecord { Id = Guid.NewGuid(), NoteId = row.Id, Body = row.Body, Version = row.Version, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return new NoteInfo(row.Id, row.OrgId, row.OwnerUserId, row.Title, row.Body, row.Version,
            row.ConversationId, row.KnowledgeLink, row.CreatedAt, row.UpdatedAt);
    }

    public async Task<IReadOnlyList<NotificationInfo>> ListNotificationsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.CollabNotifications.AsNoTracking()
            .Where(n => n.UserId == U(userId)).ToListAsync(ct);
        return rows.OrderByDescending(n => n.CreatedAt).Take(200)
            .Select(n => new NotificationInfo(n.Id, n.OrgId, n.UserId, n.Type, n.Title, n.Body,
                n.DeepLink, n.ReadAt != null, n.CreatedAt)).ToList();
    }

    public async Task<bool> MarkNotificationReadAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var row = await db.CollabNotifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == U(userId), ct);
        if (row is null) return false;
        row.ReadAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CalendarEventInfo>> ListCalendarEventsAsync(Guid userId, string? repository, string? status, CancellationToken ct)
    {
        var uid = U(userId);
        var automations = await db.Automations.AsNoTracking()
            .Where(a => a.CreatedByUserId == uid)
            .Select(a => new { a.Id, a.Name, a.Enabled, a.SelectedRepository, a.NextRunAt, a.CreatedAt })
            .Take(200).ToListAsync(ct);
        var runs = (await db.WorkflowRuns.AsNoTracking()
            .Select(r => new { r.Id, r.WorkflowId, r.Status, r.CurrentStepName, r.CreatedAt, r.StartedAt, r.FinishedAt })
            .ToListAsync(ct))
            .OrderByDescending(r => r.CreatedAt).Take(200).ToList();
        var result = automations.Select(a => new CalendarEventInfo(
            $"automation:{a.Id}", "automation", a.Name, a.Enabled ? "scheduled" : "disabled",
            a.SelectedRepository, uid, a.NextRunAt ?? a.CreatedAt, null, "/automations"))
            .Concat(runs.Select(r => new CalendarEventInfo(
                $"workflow:{r.Id}", "workflow", r.CurrentStepName ?? "workflow run", r.Status,
                null, null, r.StartedAt ?? r.CreatedAt, r.FinishedAt,
                $"/workflows/{r.WorkflowId}"))).ToList();
        if (!string.IsNullOrWhiteSpace(repository)) result = result.Where(e => e.Repository == repository).ToList();
        if (!string.IsNullOrWhiteSpace(status)) result = result.Where(e => e.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
        return result.OrderBy(e => e.StartAt).ToList();
    }
}