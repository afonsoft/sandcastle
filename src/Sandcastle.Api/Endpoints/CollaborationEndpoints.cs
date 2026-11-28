using Sandcastle.Application.Collaboration;
using Sandcastle.Application.Identity;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-186: collaboration workspace channels/notes/notifications/calendar.</summary>
public static class CollaborationEndpoints
{
    /// <summary>Maps collaboration endpoints.</summary>
    public static IEndpointRouteBuilder MapCollaborationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var channels = app.MapGroup("/api/v1/channels").WithTags(EndpointTags.Api).RequireAuthorization();
        channels.MapGet("", ListChannelsAsync);
        channels.MapPost("", CreateChannelAsync);
        channels.MapGet("/{id:guid}/messages", ListMessagesAsync);
        channels.MapPost("/{id:guid}/messages", PostMessageAsync);
        channels.MapPost("/{id:guid}/read", MarkChannelReadAsync);
        channels.MapPost("/{id:guid}/pin", PinChannelAsync);
        app.MapPost("/api/v1/channel-messages/{id:guid}/reactions", ReactAsync)
            .WithTags(EndpointTags.Api).RequireAuthorization();

        var notes = app.MapGroup("/api/v1/notes").WithTags(EndpointTags.Api).RequireAuthorization();
        notes.MapGet("", ListNotesAsync);
        notes.MapPost("", CreateNoteAsync);
        notes.MapPut("/{id:guid}", UpdateNoteAsync);

        var notifications = app.MapGroup("/api/v1/notifications").WithTags(EndpointTags.Api).RequireAuthorization();
        notifications.MapGet("", ListNotificationsAsync);
        notifications.MapPost("/{id:guid}/read", MarkNotificationReadAsync);

        app.MapGet("/api/v1/calendar/events", ListCalendarEventsAsync)
            .WithTags(EndpointTags.Api).RequireAuthorization();
        return app;
    }

    private static bool User(ICurrentUserAccessor current, out Guid userId)
    {
        userId = current.User?.UserId ?? Guid.Empty;
        return userId != Guid.Empty;
    }

    private static async Task<IResult> ListChannelsAsync(
        ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized() : Results.Ok(await svc.ListChannelsAsync(uid, ct));

    private static async Task<IResult> CreateChannelAsync(
        ChannelUpsert body, ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct)
    {
        if (!User(current, out var uid)) return Results.Unauthorized();
        try { return Results.Created($"/api/v1/channels", await svc.CreateChannelAsync(body, uid, ct)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }

    private static async Task<IResult> ListMessagesAsync(
        Guid id, ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized() : Results.Ok(await svc.ListMessagesAsync(id, uid, ct));

    private static async Task<IResult> PostMessageAsync(
        Guid id, ChannelMessageUpsert body, ICurrentUserAccessor current,
        ICollaborationService svc, CancellationToken ct)
    {
        if (!User(current, out var uid)) return Results.Unauthorized();
        var row = await svc.PostMessageAsync(id, body, uid, ct);
        return row is null ? Results.NotFound() : Results.Created($"/api/v1/channels/{id}/messages/{row.Id}", row);
    }

    private static async Task<IResult> ReactAsync(
        Guid id, ReactionRequest body, ICurrentUserAccessor current,
        ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized()
        : await svc.ReactAsync(id, body.Emoji, uid, ct) ? Results.Accepted() : Results.NotFound();

    private static async Task<IResult> MarkChannelReadAsync(
        Guid id, ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized()
        : await svc.MarkChannelReadAsync(id, uid, ct) ? Results.Accepted() : Results.NotFound();

    private static async Task<IResult> PinChannelAsync(
        Guid id, PinRequest body, ICurrentUserAccessor current,
        ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized()
        : await svc.PinChannelAsync(id, body.Pinned, uid, ct) ? Results.Accepted() : Results.NotFound();

    private static async Task<IResult> ListNotesAsync(
        ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized() : Results.Ok(await svc.ListNotesAsync(uid, ct));

    private static async Task<IResult> CreateNoteAsync(
        NoteUpsert body, ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct)
    {
        if (!User(current, out var uid)) return Results.Unauthorized();
        try { return Results.Created("/api/v1/notes", await svc.UpsertNoteAsync(null, body, uid, ct)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }

    private static async Task<IResult> UpdateNoteAsync(
        Guid id, NoteUpsert body, ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct)
    {
        if (!User(current, out var uid)) return Results.Unauthorized();
        try { return Results.Ok(await svc.UpsertNoteAsync(id, body, uid, ct)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }

    private static async Task<IResult> ListNotificationsAsync(
        ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized() : Results.Ok(await svc.ListNotificationsAsync(uid, ct));

    private static async Task<IResult> MarkNotificationReadAsync(
        Guid id, ICurrentUserAccessor current, ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized()
        : await svc.MarkNotificationReadAsync(id, uid, ct) ? Results.Accepted() : Results.NotFound();

    private static async Task<IResult> ListCalendarEventsAsync(
        string? repository, string? status, ICurrentUserAccessor current,
        ICollaborationService svc, CancellationToken ct) =>
        !User(current, out var uid) ? Results.Unauthorized()
        : Results.Ok(await svc.ListCalendarEventsAsync(uid, repository, status, ct));

    public sealed record ReactionRequest(string Emoji);
    public sealed record PinRequest(bool Pinned);
}