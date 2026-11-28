namespace Sandcastle.Application.Collaboration;

public sealed record ChannelInfo(Guid Id, Guid? OrgId, string OwnerUserId, string Name, string Type, bool Pinned, int UnreadCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ChannelUpsert(string Name, string Type, Guid? OrgId);
public sealed record ChannelMessageInfo(Guid Id, Guid ChannelId, string SenderUserId, string Body, Guid? ParentId, Guid? ConversationId, string ReactionsJson, DateTimeOffset CreatedAt);
public sealed record ChannelMessageUpsert(string Body, Guid? ParentId, Guid? ConversationId);
public sealed record NoteInfo(Guid Id, Guid? OrgId, string OwnerUserId, string Title, string Body, int Version, Guid? ConversationId, string? KnowledgeLink, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record NoteUpsert(string Title, string Body, Guid? OrgId, Guid? ConversationId, string? KnowledgeLink);
public sealed record NotificationInfo(Guid Id, Guid? OrgId, string UserId, string Type, string Title, string? Body, string? DeepLink, bool Read, DateTimeOffset CreatedAt);
public sealed record CalendarEventInfo(string Id, string Source, string Title, string Status, string? Repository, string? UserId, DateTimeOffset StartAt, DateTimeOffset? EndAt, string DeepLink);

public interface ICollaborationService
{
    Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync(Guid userId, CancellationToken ct);
    Task<ChannelInfo> CreateChannelAsync(ChannelUpsert request, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<ChannelMessageInfo>> ListMessagesAsync(Guid channelId, Guid userId, CancellationToken ct);
    Task<ChannelMessageInfo?> PostMessageAsync(Guid channelId, ChannelMessageUpsert request, Guid userId, CancellationToken ct);
    Task<bool> ReactAsync(Guid messageId, string emoji, Guid userId, CancellationToken ct);
    Task<bool> MarkChannelReadAsync(Guid channelId, Guid userId, CancellationToken ct);
    Task<bool> PinChannelAsync(Guid channelId, bool pinned, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<NoteInfo>> ListNotesAsync(Guid userId, CancellationToken ct);
    Task<NoteInfo> UpsertNoteAsync(Guid? id, NoteUpsert request, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<NotificationInfo>> ListNotificationsAsync(Guid userId, CancellationToken ct);
    Task<bool> MarkNotificationReadAsync(Guid id, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<CalendarEventInfo>> ListCalendarEventsAsync(Guid userId, string? repository, string? status, CancellationToken ct);
}