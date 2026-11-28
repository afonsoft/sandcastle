using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Infrastructure.Collaboration;
using Sandcastle.Infrastructure.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.UnitTests;

public sealed class Spec186CollaborationTests
{
    private static (CollaborationService Svc, SqliteConnection Conn) Make()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).UseSnakeCaseNamingConvention().Options;
        var db = new AppDbContext(opts);
        db.Database.ExecuteSqlRaw("""
CREATE TABLE org_members(org_id TEXT NOT NULL,user_id TEXT NOT NULL,role TEXT NOT NULL,source TEXT NOT NULL,usage_tier_id TEXT NULL,created_at TEXT NOT NULL);
CREATE TABLE collab_channels(id TEXT NOT NULL PRIMARY KEY,org_id TEXT NULL,owner_user_id TEXT NOT NULL,name TEXT NOT NULL,type TEXT NOT NULL,pinned_user_ids_json TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE collab_channel_messages(id TEXT NOT NULL PRIMARY KEY,channel_id TEXT NOT NULL,sender_user_id TEXT NOT NULL,body TEXT NOT NULL,parent_id TEXT NULL,conversation_id TEXT NULL,reactions_json TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE TABLE collab_channel_reads(channel_id TEXT NOT NULL,user_id TEXT NOT NULL,last_read_at TEXT NOT NULL,PRIMARY KEY(channel_id,user_id));
CREATE TABLE collab_notes(id TEXT NOT NULL PRIMARY KEY,org_id TEXT NULL,owner_user_id TEXT NOT NULL,title TEXT NOT NULL,body TEXT NOT NULL,version INTEGER NOT NULL,conversation_id TEXT NULL,knowledge_link TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE collab_note_versions(id TEXT NOT NULL PRIMARY KEY,note_id TEXT NOT NULL,body TEXT NOT NULL,version INTEGER NOT NULL,created_at TEXT NOT NULL);
CREATE TABLE collab_notifications(id TEXT NOT NULL PRIMARY KEY,org_id TEXT NULL,user_id TEXT NOT NULL,type TEXT NOT NULL,title TEXT NOT NULL,body TEXT NULL,deep_link TEXT NULL,read_at TEXT NULL,created_at TEXT NOT NULL);
CREATE TABLE automations(org_id TEXT NULL,id TEXT NOT NULL PRIMARY KEY,name TEXT NOT NULL,enabled INTEGER NOT NULL,selected_repository TEXT NULL,next_run_at TEXT NULL,created_at TEXT NOT NULL,created_by_user_id TEXT NOT NULL);
CREATE TABLE workflow_runs(id TEXT NOT NULL PRIMARY KEY,workflow_id TEXT NOT NULL,status TEXT NOT NULL,current_step_name TEXT NULL,created_at TEXT NOT NULL,started_at TEXT NULL,finished_at TEXT NULL);
""");
        return (new CollaborationService(db, new AuditService(db)), conn);
    }

    [Fact]
    public async Task Channels_Messages_Read_And_Reactions_RoundTrip()
    {
        var (svc, conn) = Make();
        using var _ = conn;
        var owner = Guid.NewGuid();
        var channel = await svc.CreateChannelAsync(new("general", "group", null), owner, default);
        var message = await svc.PostMessageAsync(channel.Id, new("hello run", null, null), owner, default);
        message.ShouldNotBeNull();
        await svc.ReactAsync(message!.Id, "thumbsup", owner, default);
        await svc.MarkChannelReadAsync(channel.Id, owner, default);
        var channels = await svc.ListChannelsAsync(owner, default);
        channels.Single().UnreadCount.ShouldBe(0);
        message.Body.ShouldBe("hello run");
    }

    [Fact]
    public async Task Notes_Increment_Version_On_Save()
    {
        var (svc, conn) = Make();
        using var _ = conn;
        var user = Guid.NewGuid();
        var note = await svc.UpsertNoteAsync(null, new("Runbook", "v1", null, null, null), user, default);
        note.Version.ShouldBe(1);
        note = await svc.UpsertNoteAsync(note.Id, new("Runbook", "v2", null, null, null), user, default);
        note.Version.ShouldBe(2);
        (await svc.ListNotesAsync(user, default)).Single().Body.ShouldBe("v2");
    }

    [Fact]
    public async Task Notifications_And_Calendar_Are_Scoped()
    {
        var (svc, conn) = Make();
        using var _ = conn;
        var user = Guid.NewGuid();
        (await svc.ListNotificationsAsync(user, default)).ShouldBeEmpty();
        (await svc.ListCalendarEventsAsync(user, null, null, default)).ShouldBeEmpty();
    }
}