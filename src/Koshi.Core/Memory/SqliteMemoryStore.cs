namespace Koshi.Core.Memory;

using System.Globalization;
using System.Text.Json;
using Koshi.Core.Retrieval;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite-backed memory store. Persists memories across sessions.
/// Embedding search is brute-force cosine (fine for &lt;10K memories).
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore, IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public SqliteMemoryStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(ct);

        var sql = """
            CREATE TABLE IF NOT EXISTS memories (
                id TEXT PRIMARY KEY,
                type INTEGER NOT NULL,
                content TEXT NOT NULL,
                subject TEXT NOT NULL,
                user_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL DEFAULT 'default',
                thread_id TEXT,
                source TEXT NOT NULL,
                confidence REAL NOT NULL,
                created_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                access_count INTEGER NOT NULL DEFAULT 0,
                tier INTEGER NOT NULL DEFAULT 0,
                compressed_content TEXT,
                superseded_by TEXT,
                contradiction_note TEXT,
                embedding BLOB,
                embedding_model TEXT,
                embedding_dimensions INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_memories_scope
                ON memories(user_id, workspace_id);

            CREATE INDEX IF NOT EXISTS idx_memories_type
                ON memories(type);

            CREATE INDEX IF NOT EXISTS idx_memories_subject
                ON memories(subject);

            CREATE INDEX IF NOT EXISTS idx_memories_tier
                ON memories(tier);
            """;

        using var cmd = new SqliteCommand(sql, _connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task StoreAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        EnsureConnected();
        var sql = """
            INSERT OR REPLACE INTO memories
                (id, type, content, subject, user_id, workspace_id, thread_id,
                 source, confidence, created_at, last_accessed_at, access_count,
                 tier, compressed_content, superseded_by, contradiction_note,
                 embedding, embedding_model, embedding_dimensions)
            VALUES
                (@id, @type, @content, @subject, @userId, @workspaceId, @threadId,
                 @source, @confidence, @createdAt, @lastAccessedAt, @accessCount,
                 @tier, @compressedContent, @supersededBy, @contradictionNote,
                 @embedding, @embeddingModel, @embeddingDimensions)
            """;

        using var cmd = new SqliteCommand(sql, _connection);
        BindMemoryParams(cmd, memory);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task StoreAsync(IReadOnlyList<MemoryRecord> memories, CancellationToken ct = default)
    {
        EnsureConnected();
        using var transaction = _connection!.BeginTransaction();
        try
        {
            foreach (var memory in memories)
            {
                ct.ThrowIfCancellationRequested();
                var sql = """
                    INSERT OR REPLACE INTO memories
                        (id, type, content, subject, user_id, workspace_id, thread_id,
                         source, confidence, created_at, last_accessed_at, access_count,
                         tier, compressed_content, superseded_by, contradiction_note,
                         embedding, embedding_model, embedding_dimensions)
                    VALUES
                        (@id, @type, @content, @subject, @userId, @workspaceId, @threadId,
                         @source, @confidence, @createdAt, @lastAccessedAt, @accessCount,
                         @tier, @compressedContent, @supersededBy, @contradictionNote,
                         @embedding, @embeddingModel, @embeddingDimensions)
                    """;
                using var cmd = new SqliteCommand(sql, _connection);
                cmd.Transaction = transaction;
                BindMemoryParams(cmd, memory);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        EnsureConnected();
        var sql = "SELECT * FROM memories WHERE id = @id";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadMemory(reader) : null;
    }

    public async Task<IReadOnlyList<MemoryRecord>> GetByScopeAsync(
        MemoryScope scope, MemoryType? type = null, MemoryTier? tier = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var conditions = new List<string>();
        var parameters = new List<SqliteParameter>();

        // Scope filtering: match user or global (*)
        conditions.Add("(user_id = @userId OR user_id = '*')");
        parameters.Add(new("@userId", scope.UserId));

        conditions.Add("workspace_id = @workspaceId");
        parameters.Add(new("@workspaceId", scope.WorkspaceId));

        if (scope.ThreadId is not null)
        {
            conditions.Add("(thread_id = @threadId OR thread_id IS NULL)");
            parameters.Add(new("@threadId", scope.ThreadId));
        }

        if (type.HasValue)
        {
            conditions.Add("type = @type");
            parameters.Add(new("@type", (int)type.Value));
        }

        if (tier.HasValue)
        {
            conditions.Add("tier = @tier");
            parameters.Add(new("@tier", (int)tier.Value));
        }

        // Exclude superseded memories
        conditions.Add("superseded_by IS NULL");

        var sql = $"SELECT * FROM memories WHERE {string.Join(" AND ", conditions)} ORDER BY last_accessed_at DESC";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddRange(parameters.ToArray());

        var results = new List<MemoryRecord>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadMemory(reader));

        return results;
    }

    public async Task<IReadOnlyList<MemoryRecord>> SearchByEmbeddingAsync(
        float[] queryEmbedding, MemoryScope scope, int topK = 10, CancellationToken ct = default)
    {
        // Load all in-scope memories with embeddings, compute cosine similarity
        var candidates = await GetByScopeAsync(scope, ct: ct);
        if (candidates.Count == 0) return [];

        var scored = new List<(MemoryRecord Memory, float Score)>();
        int dimensionMismatches = 0;
        foreach (var memory in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (memory.Embedding is null) continue;
            if (memory.Embedding.Length != queryEmbedding.Length)
            {
                dimensionMismatches++;
                continue;
            }

            float similarity = Similarity.Cosine(queryEmbedding, memory.Embedding);
            scored.Add((memory, similarity));
        }

        // Log dimension mismatches (indicates embedding model change)
        if (dimensionMismatches > 0)
            System.Diagnostics.Trace.TraceWarning(
                $"Skipped {dimensionMismatches} memories with dimension mismatch (query={queryEmbedding.Length}).");

        return scored
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Memory)
            .ToList();
    }

    public async Task UpdateAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        // Reuse store — INSERT OR REPLACE handles update
        await StoreAsync(memory, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        EnsureConnected();
        var sql = "DELETE FROM memories WHERE id = @id";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountAsync(MemoryScope? scope = null, CancellationToken ct = default)
    {
        EnsureConnected();
        string sql;
        using var cmd = new SqliteCommand();
        cmd.Connection = _connection;

        if (scope is not null)
        {
            sql = "SELECT COUNT(*) FROM memories WHERE (user_id = @userId OR user_id = '*') AND workspace_id = @workspaceId";
            cmd.Parameters.AddWithValue("@userId", scope.UserId);
            cmd.Parameters.AddWithValue("@workspaceId", scope.WorkspaceId);
        }
        else
        {
            sql = "SELECT COUNT(*) FROM memories";
        }

        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task RecordAccessAsync(string id, CancellationToken ct = default)
    {
        EnsureConnected();
        var sql = """
            UPDATE memories
            SET access_count = access_count + 1,
                last_accessed_at = @now
            WHERE id = @id
            """;
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    private void EnsureConnected()
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Memory store not initialized. Call InitializeAsync first.");
    }

    private static void BindMemoryParams(SqliteCommand cmd, MemoryRecord m)
    {
        cmd.Parameters.AddWithValue("@id", m.Id);
        cmd.Parameters.AddWithValue("@type", (int)m.Type);
        cmd.Parameters.AddWithValue("@content", m.Content);
        cmd.Parameters.AddWithValue("@subject", m.Subject);
        cmd.Parameters.AddWithValue("@userId", m.Scope.UserId);
        cmd.Parameters.AddWithValue("@workspaceId", m.Scope.WorkspaceId);
        cmd.Parameters.AddWithValue("@threadId", (object?)m.Scope.ThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@source", m.Source);
        cmd.Parameters.AddWithValue("@confidence", m.Confidence);
        cmd.Parameters.AddWithValue("@createdAt", m.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@lastAccessedAt", m.LastAccessedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@accessCount", m.AccessCount);
        cmd.Parameters.AddWithValue("@tier", (int)m.Tier);
        cmd.Parameters.AddWithValue("@compressedContent", (object?)m.CompressedContent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@supersededBy", (object?)m.SupersededBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@contradictionNote", (object?)m.ContradictionNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embedding", m.Embedding is not null ? SerializeEmbedding(m.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@embeddingModel", (object?)m.EmbeddingModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embeddingDimensions", m.EmbeddingDimensions);
    }

    private static MemoryRecord ReadMemory(SqliteDataReader reader)
    {
        var embedding = reader.IsDBNull(reader.GetOrdinal("embedding"))
            ? null
            : DeserializeEmbedding((byte[])reader["embedding"]);

        return new MemoryRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Type = (MemoryType)reader.GetInt32(reader.GetOrdinal("type")),
            Content = reader.GetString(reader.GetOrdinal("content")),
            Subject = reader.GetString(reader.GetOrdinal("subject")),
            Scope = new MemoryScope(
                reader.GetString(reader.GetOrdinal("user_id")),
                reader.GetString(reader.GetOrdinal("workspace_id")),
                reader.IsDBNull(reader.GetOrdinal("thread_id")) ? null : reader.GetString(reader.GetOrdinal("thread_id"))),
            Source = reader.GetString(reader.GetOrdinal("source")),
            Confidence = reader.GetFloat(reader.GetOrdinal("confidence")),
            CreatedAt = DateTimeOffset.ParseExact(reader.GetString(reader.GetOrdinal("created_at")), "O", CultureInfo.InvariantCulture),
            LastAccessedAt = DateTimeOffset.ParseExact(reader.GetString(reader.GetOrdinal("last_accessed_at")), "O", CultureInfo.InvariantCulture),
            AccessCount = reader.GetInt32(reader.GetOrdinal("access_count")),
            Tier = (MemoryTier)reader.GetInt32(reader.GetOrdinal("tier")),
            CompressedContent = reader.IsDBNull(reader.GetOrdinal("compressed_content")) ? null : reader.GetString(reader.GetOrdinal("compressed_content")),
            SupersededBy = reader.IsDBNull(reader.GetOrdinal("superseded_by")) ? null : reader.GetString(reader.GetOrdinal("superseded_by")),
            ContradictionNote = reader.IsDBNull(reader.GetOrdinal("contradiction_note")) ? null : reader.GetString(reader.GetOrdinal("contradiction_note")),
            Embedding = embedding,
            EmbeddingModel = reader.IsDBNull(reader.GetOrdinal("embedding_model")) ? null : reader.GetString(reader.GetOrdinal("embedding_model")),
            EmbeddingDimensions = reader.GetInt32(reader.GetOrdinal("embedding_dimensions")),
        };
    }

    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
