using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Indexing;
using Sandcastle.Application.Knowledge;
using Sandcastle.Application.Web;
using Sandcastle.Infrastructure.Knowledge;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.UnitTests;

public sealed class Spec189KnowledgeRagTests
{
    private sealed class FakeEmbeddings : IEmbeddingProvider
    {
        public bool IsConfigured => true;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(t => new[] { (float)t.Length, 1f }).ToArray());
    }
    private sealed class FakeWeb : IWebSearchService
    {
        public Task<WebSearchOutcome> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default) =>
            Task.FromResult(new WebSearchOutcome(true, [new WebSearchResult("web result", "https://example.test/doc", "provenance snippet")], null));
    }

    private static (KnowledgeRagService Svc, SqliteConnection Conn) Make(IEmbeddingProvider? embeddings = null, IWebSearchService? web = null)
    {
        var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).UseSnakeCaseNamingConvention().Options;
        var db = new AppDbContext(opts);
        db.Database.ExecuteSqlRaw("""
CREATE TABLE org_members(org_id TEXT NOT NULL,user_id TEXT NOT NULL,role TEXT NOT NULL,source TEXT NOT NULL,usage_tier_id TEXT NULL,created_at TEXT NOT NULL);
CREATE TABLE knowledge_collections(id TEXT NOT NULL PRIMARY KEY,org_id TEXT NULL,owner_user_id TEXT NOT NULL,name TEXT NOT NULL,repo_path TEXT NULL,description TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE knowledge_documents(id TEXT NOT NULL PRIMARY KEY,collection_id TEXT NOT NULL,title TEXT NOT NULL,source_uri TEXT NOT NULL,content_hash TEXT NOT NULL,chunk_count INTEGER NOT NULL,lineage_json TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE TABLE knowledge_chunks(id TEXT NOT NULL PRIMARY KEY,document_id TEXT NOT NULL,ordinal INTEGER NOT NULL,text TEXT NOT NULL,embedding_json TEXT NULL,created_at TEXT NOT NULL);
CREATE TABLE memory_records(id TEXT NOT NULL PRIMARY KEY,org_id TEXT NOT NULL,key TEXT NOT NULL,text TEXT NOT NULL,created_by_user_id TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,deleted_at TEXT NULL);
""");
        return (new KnowledgeRagService(db, embeddings, web), conn);
    }

    [Fact]
    public async Task Ingestion_Chunks_Redacts_And_Hybrid_Query_Cites()
    {
        var (svc, conn) = Make(new FakeEmbeddings()); using var _ = conn;
        var user = Guid.NewGuid();
        var collection = await svc.CreateCollectionAsync(new("repo docs", "owner/repo", null, null), user, default);
        var content = "deployment runbook\nrollback instructions\napi_key=supersecretvalue";
        var doc = await svc.IngestDocumentAsync(collection.Id, new("runbook", "repo://runbook.md", content), user, default);
        doc.ShouldNotBeNull(); doc!.ChunkCount.ShouldBe(1); doc.LineageJson.ShouldContain("repo://runbook.md");
        var response = await svc.QueryAsync(new("rollback", collection.Id, null, 5), user, default);
        response.Mode.ShouldBe("hybrid"); response.EmbeddingAvailable.ShouldBeTrue();
        response.Citations.ShouldContain(c => c.Excerpt.Contains("rollback") && !c.Excerpt.Contains("supersecretvalue"));
    }

    [Fact]
    public async Task Query_Without_Embedding_Falls_Back_Lexically_And_Web_Adds_Provenance()
    {
        var (svc, conn) = Make(null, new FakeWeb()); using var _ = conn;
        var user = Guid.NewGuid();
        var collection = await svc.CreateCollectionAsync(new("docs", null, null, null), user, default);
        await svc.IngestDocumentAsync(collection.Id, new("note", "manual:note", "local knowledge"), user, default);
        var response = await svc.QueryAsync(new("knowledge", null, null, 5, IncludeWeb: true), user, default);
        response.Mode.ShouldBe("lexical+web");
        response.Citations.ShouldContain(c => c.SourceUri == "https://example.test/doc");
    }

    [Fact]
    public async Task Query_Includes_User_Memory_Pipeline_Records()
    {
        var (svc, conn) = Make(); using var _ = conn;
        var user = Guid.NewGuid();
        await using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO memory_records VALUES($id,$org,$key,$text,$actor,$created,$updated,NULL)";
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); insert.Parameters.AddWithValue("$org", Guid.Empty.ToString());
        insert.Parameters.AddWithValue("$key", "deploy-note"); insert.Parameters.AddWithValue("$text", "remember rollback playbook");
        insert.Parameters.AddWithValue("$actor", user.ToString()); insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O")); insert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync();
        var response = await svc.QueryAsync(new("rollback", null, null, 5), user, default);
        response.Citations.ShouldContain(c => c.SourceUri == "memory:deploy-note");
    }

    [Fact]
    public async Task Collections_Are_Owner_Scoped_And_Delete_Cascades()
    {
        var (svc, conn) = Make(); using var _ = conn;
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var collection = await svc.CreateCollectionAsync(new("private", null, null, null), owner, default);
        (await svc.ListCollectionsAsync(other, default)).ShouldBeEmpty();
        (await svc.IngestDocumentAsync(collection.Id, new("x", "manual:x", "secret"), other, default)).ShouldBeNull();
        (await svc.DeleteCollectionAsync(collection.Id, owner, default)).ShouldBeTrue();
        (await svc.ListCollectionsAsync(owner, default)).ShouldBeEmpty();
    }
}