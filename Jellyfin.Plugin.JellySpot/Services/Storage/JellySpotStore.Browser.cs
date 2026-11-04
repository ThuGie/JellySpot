using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellySpot.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public partial class JellySpotStore
{
    public static string Revision(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private async Task<SqliteConnection> OpenBrowserAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Key, object? Value)[] args)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private void MigrateBrowser(SqliteConnection connection)
    {
        using var check = Command(connection, "PRAGMA user_version");
        if (Convert.ToInt32(check.ExecuteScalar()) < 2)
        {
            using var backup = new SqliteConnection($"Data Source={_dbPath}.pre-browser.bak");
            backup.Open(); connection.BackupDatabase(backup);
            using var tx = connection.BeginTransaction();
            void Execute(string sql) { using var cmd = Command(connection, sql); cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
            foreach (var (table, name, type) in new[] {
                ("queue_requests","request_id","TEXT"),("queue_requests","requested_at","TEXT"),
                ("queue_requests","state","TEXT NOT NULL DEFAULT 'Active'"),("queue_requests","priority","INTEGER NOT NULL DEFAULT 0"),
                ("queue_requests","hidden","INTEGER NOT NULL DEFAULT 0"),("collections","imported_at","TEXT"),
                ("collections","shared_at","TEXT"),("collections","track_count","INTEGER NOT NULL DEFAULT 0"),
                ("collection_follows","followed_at","TEXT") })
            {
                if (!ColumnExists(connection, table, name, tx)) Execute($"ALTER TABLE {table} ADD COLUMN {name} {type}");
            }
            Execute("""
                UPDATE queue_requests SET request_id=lower(hex(randomblob(16))) WHERE request_id IS NULL;
                UPDATE collections SET track_count=coalesce(json_array_length(json,'$.Tracks'),0);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_request_id ON queue_requests(request_id);
                CREATE INDEX IF NOT EXISTS ix_queue_status ON queue(status,updated_at);
                CREATE INDEX IF NOT EXISTS ix_collections_owner ON collections(owner_id,shared);
                CREATE TABLE IF NOT EXISTS browser_entries(
                  scope TEXT NOT NULL, entry_id TEXT NOT NULL, position INTEGER NOT NULL, track_id TEXT NOT NULL,
                  title TEXT NOT NULL, artist TEXT NOT NULL, album TEXT NOT NULL, duration INTEGER NOT NULL,
                  added_at TEXT, json TEXT NOT NULL, PRIMARY KEY(scope,entry_id));
                CREATE INDEX IF NOT EXISTS ix_entries_track ON browser_entries(track_id);
                CREATE INDEX IF NOT EXISTS ix_entries_order ON browser_entries(scope,position);
                CREATE TABLE IF NOT EXISTS browser_snapshots(
                  user_id TEXT NOT NULL, generation TEXT NOT NULL, kind TEXT NOT NULL, complete INTEGER NOT NULL,
                  updated_at TEXT NOT NULL, PRIMARY KEY(user_id,generation,kind));
                CREATE TABLE IF NOT EXISTS browser_catalog(
                  user_id TEXT NOT NULL,generation TEXT NOT NULL,kind TEXT NOT NULL,id TEXT NOT NULL,
                  name TEXT NOT NULL,added_at TEXT,json TEXT NOT NULL,PRIMARY KEY(user_id,generation,kind,id));
                CREATE TABLE IF NOT EXISTS operations(
                  id TEXT PRIMARY KEY,user_id TEXT NOT NULL,kind TEXT NOT NULL,status TEXT NOT NULL,
                  idempotency_key TEXT NOT NULL,json TEXT NOT NULL,updated_at TEXT NOT NULL,
                  UNIQUE(user_id,idempotency_key));
                CREATE INDEX IF NOT EXISTS ix_operations_status ON operations(status,updated_at);
                CREATE TABLE IF NOT EXISTS download_preferences(user_id TEXT PRIMARY KEY,paused INTEGER NOT NULL DEFAULT 0);
                INSERT OR IGNORE INTO browser_entries(scope,entry_id,position,track_id,title,artist,album,duration,added_at,json)
                SELECT c.id,printf('%08d',t.key),t.key,json_extract(t.value,'$.Id'),coalesce(json_extract(t.value,'$.Name'),''),
                  coalesce((SELECT group_concat(value,', ') FROM json_each(t.value,'$.Artists')),''),
                  coalesce(json_extract(t.value,'$.Album'),''),coalesce(json_extract(t.value,'$.DurationMs'),0),
                  json_extract(t.value,'$.AddedAtUtc'),t.value
                FROM collections c,json_each(c.json,'$.Tracks') t WHERE json_extract(t.value,'$.Id') IS NOT NULL;
                PRAGMA user_version=2;
                """);
            tx.Commit();
        }
        if (Convert.ToInt32(check.ExecuteScalar()) < 3)
        {
            using var tx = connection.BeginTransaction();
            using var checkpointSchema = Command(connection, "CREATE TABLE IF NOT EXISTS operation_tracks(operation_id TEXT NOT NULL,track_id TEXT NOT NULL,source_id TEXT NOT NULL,outcome INTEGER NOT NULL,PRIMARY KEY(operation_id,track_id,source_id)); PRAGMA user_version=3;");
            checkpointSchema.Transaction = tx; checkpointSchema.ExecuteNonQuery(); tx.Commit();
        }
        using var recover = Command(connection, "UPDATE operations SET status='Queued',json=json_set(json,'$.Status','Queued','$.Stage','Resuming after restart') WHERE status='Running'");
        recover.ExecuteNonQuery();
    }

    public async Task<BrowserOperation> AcceptOperationAsync(Guid userId, string generation, OperationInput input, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var c = await OpenBrowserAsync(ct);
            var key = string.IsNullOrWhiteSpace(input.IdempotencyKey) ? Guid.NewGuid().ToString("N") : input.IdempotencyKey;
            using var existing = Command(c, "SELECT json FROM operations WHERE user_id=$user AND (idempotency_key=$key OR ($kind IN ('sync','refresh','inspect-playlist','inspect-album') AND kind=$kind AND coalesce(json_extract(json,'$.Input.SourceId'),'')=$source AND json_extract(json,'$.Generation')=$gen AND coalesce(json_extract(json,'$.CancelRequested'),0)=0 AND status IN ('Queued','Running'))) LIMIT 1", ("$user", userId.ToString("N")), ("$key", key), ("$kind", input.Kind), ("$source", input.SourceId ?? ""), ("$gen", generation));
            if (await existing.ExecuteScalarAsync(ct) is string json) return JsonSerializer.Deserialize<BrowserOperation>(json)!;
            var operation = new BrowserOperation { UserId = userId, Generation = generation, Input = input };
            using var insert = Command(c, "INSERT INTO operations(id,user_id,kind,status,idempotency_key,json,updated_at) VALUES($id,$user,$kind,'Queued',$key,$json,$updated)", ("$id", operation.Id), ("$user", userId.ToString("N")), ("$kind", input.Kind), ("$key", key), ("$json", JsonSerializer.Serialize(operation)), ("$updated", operation.UpdatedAtUtc.ToString("O")));
            await insert.ExecuteNonQueryAsync(ct); return operation;
        }
        finally { _lock.Release(); }
    }
    public async Task<List<BrowserOperation>> OperationsAsync(Guid? userId, bool queuedOnly = false, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct);
        using var cmd = Command(c, "SELECT json FROM operations WHERE ($user IS NULL OR user_id=$user) AND ($queued=0 OR status='Queued') ORDER BY updated_at " + (queuedOnly ? "ASC" : "DESC") + " LIMIT 100", ("$user", userId?.ToString("N")), ("$queued", queuedOnly));
        using var reader = await cmd.ExecuteReaderAsync(ct); var result = new List<BrowserOperation>();
        while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<BrowserOperation>(reader.GetString(0))!);
        return result;
    }
    public async Task<BrowserOperation?> OperationAsync(string id, Guid? userId, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); using var cmd = Command(c, "SELECT json FROM operations WHERE id=$id AND ($user IS NULL OR user_id=$user)", ("$id", id), ("$user", userId?.ToString("N")));
        return await cmd.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<BrowserOperation>(json) : null;
    }
    public async Task SaveOperationAsync(BrowserOperation operation, CancellationToken ct = default)
    {
        operation.UpdatedAtUtc = DateTime.UtcNow;
        await using var c = await OpenBrowserAsync(ct);
        using var cmd = Command(c, "UPDATE operations SET status=$status,updated_at=$updated,json=json_set($json,'$.CancelRequested',coalesce(json_extract(json,'$.CancelRequested'),0)) WHERE id=$id", ("$id", operation.Id), ("$status", operation.Status), ("$updated", operation.UpdatedAtUtc.ToString("O")), ("$json", JsonSerializer.Serialize(operation)));
        // Preserve a cancellation racing a progress update; JSON booleans must remain JSON booleans.
        cmd.CommandText = "UPDATE operations SET status=$status,updated_at=$updated,json=CASE WHEN json_extract(json,'$.CancelRequested')=1 THEN json_set($json,'$.CancelRequested',json('true')) ELSE $json END WHERE id=$id";
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> CancelOperationAsync(string id, Guid userId, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct);
        using var cmd = Command(c, "UPDATE operations SET json=json_set(json,'$.CancelRequested',json('true')) WHERE id=$id AND user_id=$user AND status IN ('Queued','Running')", ("$id", id), ("$user", userId.ToString("N")));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
    public async Task<BrowserPage<OperationSummary>> OperationPageAsync(Guid userId, PageQuery query, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct);
        var args = new (string, object?)[] { ("$user", userId.ToString("N")), ("$filter", query.Filter), ("$q", string.IsNullOrWhiteSpace(query.Q) ? "" : "%" + query.Q + "%") };
        const string where = "user_id=$user AND ($filter='' OR status=$filter) AND ($q='' OR kind LIKE $q OR json_extract(json,'$.CurrentCollection') LIKE $q)";
        using var count = Command(c, "SELECT count(*) FROM operations WHERE " + where, args); var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        const string projection = "json_object('Id',id,'Kind',kind,'Status',status,'Stage',json_extract(json,'$.Stage'),'CurrentCollection',json_extract(json,'$.CurrentCollection'),'Processed',json_extract(json,'$.Processed'),'Discovered',json_extract(json,'$.Discovered'),'Requested',json_extract(json,'$.Requested'),'Existing',json_extract(json,'$.Existing'),'Failed',json_extract(json,'$.Failed'),'TotalKnown',json_extract(json,'$.TotalKnown'),'CancelRequested',json_extract(json,'$.CancelRequested'),'CreatedAtUtc',json_extract(json,'$.CreatedAtUtc'),'StartedAtUtc',json_extract(json,'$.StartedAtUtc'),'FinishedAtUtc',json_extract(json,'$.FinishedAtUtc'),'Error',json_extract(json,'$.Error'),'Issues',json(coalesce(json_extract(json,'$.Issues'),'[]')))";
        // SQLite stores booleans as integers; the DTO projection must emit JSON booleans.
        var summaryJson = projection.Replace("json_extract(json,'$.TotalKnown')", "json(CASE WHEN json_extract(json,'$.TotalKnown')=1 THEN 'true' ELSE 'false' END)")
            .Replace("json_extract(json,'$.CancelRequested')", "json(CASE WHEN json_extract(json,'$.CancelRequested')=1 THEN 'true' ELSE 'false' END)");
        var order = query.Sort == "activity" ? "CASE WHEN status='Running' THEN 0 WHEN status='Queued' THEN 1 ELSE 2 END," : "";
        using var cmd = Command(c, "SELECT " + summaryJson + " FROM operations WHERE " + where + " ORDER BY " + order + "updated_at DESC,id LIMIT $limit OFFSET $offset", args.Concat(new[] { ("$limit", (object?)query.Size), ("$offset", (object?)query.Offset) }).ToArray());
        using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<OperationSummary>(); while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<OperationSummary>(reader.GetString(0))!);
        return new(rows, total, Math.Max(1, query.Page), query.Size, Revision(new { rows, total }));
    }

    public async Task<bool> DownloadsPausedAsync(Guid userId, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); using var cmd = Command(c, "SELECT paused FROM download_preferences WHERE user_id=$user", ("$user", userId.ToString("N")));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0) != 0;
    }
    public async Task<int> ChangeRequestsAsync(Guid userId, RequestAction action, CancellationToken ct = default)
    {
        if (action.Action is not ("pause" or "resume" or "cancel" or "retry" or "priority" or "clear")) throw new ArgumentException("Unknown request action.");
        await _lock.WaitAsync(ct);
        try
        {
            await using var c = await OpenBrowserAsync(ct); using var tx = c.BeginTransaction(); var count = 0;
            if (action.PauseAll.HasValue)
            {
                using var preference = Command(c, "INSERT INTO download_preferences(user_id,paused) VALUES($user,$paused) ON CONFLICT(user_id) DO UPDATE SET paused=excluded.paused", ("$user", userId.ToString("N")), ("$paused", action.PauseAll.Value)); preference.Transaction = tx; await preference.ExecuteNonQueryAsync(ct);
            }
            foreach (var id in action.Ids.Distinct().Take(1000))
            {
                var assignment = action.Action switch { "pause" => "state='Paused'", "resume" or "retry" => "state='Active'", "cancel" => "state='Cancelled'", "priority" => "priority=$priority", _ => "hidden=1" };
                using var cmd = Command(c, $"UPDATE queue_requests SET {assignment} WHERE request_id=$id AND user_id=$user " + (action.Action == "clear" ? "AND (state='Cancelled' OR EXISTS(SELECT 1 FROM queue q WHERE q.id=job_id AND q.status IN ('Completed','Failed')))" : ""), ("$id", id), ("$user", userId.ToString("N")), ("$priority", Math.Clamp(action.Priority, -1, 1)));
                cmd.Transaction = tx; count += await cmd.ExecuteNonQueryAsync(ct);
                if (action.Action == "retry")
                {
                    using var retry = Command(c, "UPDATE queue SET status='Pending',json=json_set(json,'$.Status','Pending','$.Error',NULL) WHERE status='Failed' AND id IN (SELECT job_id FROM queue_requests WHERE request_id=$id AND user_id=$user)", ("$id", id), ("$user", userId.ToString("N"))); retry.Transaction = tx; await retry.ExecuteNonQueryAsync(ct);
                }
            }
            tx.Commit(); return count;
        }
        finally { _lock.Release(); }
    }
    public async Task<List<QueueItem>> EligibleJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct);
        using var cmd = Command(c, """
            SELECT q.json FROM queue q LEFT JOIN queue_requests r ON r.job_id=q.id
            LEFT JOIN download_preferences p ON p.user_id=r.user_id
            WHERE q.status='Pending' AND (json_extract(q.json,'$.AdminOverride')=1 OR (r.state='Active' AND coalesce(p.paused,0)=0))
            GROUP BY q.id ORDER BY max(CASE WHEN json_extract(q.json,'$.AdminOverride')=1 THEN 2 ELSE r.priority END) DESC,min(coalesce(r.requested_at,q.updated_at)),q.id LIMIT $limit
            """, ("$limit", limit)); using var reader = await cmd.ExecuteReaderAsync(ct); var result = new List<QueueItem>();
        while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<QueueItem>(reader.GetString(0))!); return result;
    }
    public async Task<bool> ClaimJobAsync(string id, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct);
        using var cmd = Command(c, "UPDATE queue SET status='Matching',json=json_set(json,'$.Status','Matching') WHERE id=$id AND status='Pending' AND (json_extract(json,'$.AdminOverride')=1 OR EXISTS(SELECT 1 FROM queue_requests r LEFT JOIN download_preferences p ON p.user_id=r.user_id WHERE r.job_id=queue.id AND r.state='Active' AND coalesce(p.paused,0)=0))", ("$id", id));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private const string RequestStatus = "CASE WHEN r.state='Cancelled' THEN 'Cancelled' WHEN q.status='Completed' THEN 'Completed' WHEN q.status IN ('Matching','Downloading') THEN q.status WHEN r.state='Paused' OR coalesce(p.paused,0)=1 THEN 'Paused' ELSE q.status END";
    public async Task<BrowserPage<RequestRow>> RequestPageAsync(Guid userId, PageQuery query, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct);
        var where = $"r.user_id=$user AND r.hidden=0 AND ($q='' OR json_extract(q.json,'$.Title') LIKE $q OR json_extract(q.json,'$.Artists') LIKE $q OR r.source_name LIKE $q) AND ($filter='' OR {RequestStatus}=$filter)";
        var join = " FROM queue_requests r JOIN queue q ON r.job_id=q.id LEFT JOIN download_preferences p ON p.user_id=r.user_id ";
        var args = new (string, object?)[] { ("$user", userId.ToString("N")), ("$q", string.IsNullOrEmpty(query.Q) ? "" : "%" + query.Q + "%"), ("$filter", query.Filter) };
        using var totalCmd = Command(c, "SELECT count(*)" + join + "WHERE " + where, args); var total = Convert.ToInt32(await totalCmd.ExecuteScalarAsync(ct));
        var column = query.Sort switch { "requested" => "r.requested_at", "updated" => "q.updated_at", "priority" => "r.priority", "title" => "json_extract(q.json,'$.Title') COLLATE NOCASE", "status" => RequestStatus, _ => "CASE WHEN q.status IN ('Matching','Downloading') THEN 0 WHEN q.status='Pending' AND r.state='Active' THEN 1 ELSE 2 END" };
        var direction = query.Direction == "desc" ? "DESC" : "ASC";
        using var cmd = Command(c, $"SELECT q.json,r.request_id,r.source_name,{RequestStatus},r.priority,r.requested_at,q.updated_at" + join + $"WHERE {where} ORDER BY ({column} IS NULL),{column} {direction},q.updated_at DESC,r.request_id LIMIT $limit OFFSET $offset", args.Concat(new[] { ("$limit", (object?)query.Size), ("$offset", (object?)query.Offset) }).ToArray());
        using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<RequestRow>();
        while (await reader.ReadAsync(ct))
        {
            var job = JsonSerializer.Deserialize<QueueItem>(reader.GetString(0))!;
            rows.Add(new() { Id = reader.GetString(1), TrackId = job.SpotifyTrackId, Title = job.Title, Artists = job.Artists, Album = job.Album, CoverUrl = job.CoverUrl, SourceName = reader.GetString(2), Status = reader.GetString(3), JobStatus = job.Status, Priority = reader.GetInt32(4), RequestedAtUtc = reader.IsDBNull(5) ? null : reader.GetString(5), UpdatedAtUtc = reader.GetString(6), DownloadedAtUtc = job.CompletedAtUtc?.ToString("O"), Progress = job.Progress, Stage = job.Stage, Error = job.Status == "Failed" ? "Download failed. Retry or ask an administrator." : null });
        }
        return new(rows, total, Math.Max(1, query.Page), query.Size, Revision(new { rows, total }));
    }
    public async Task<Dictionary<string, int>> RequestCountsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); using var cmd = Command(c, $"SELECT {RequestStatus},count(*) FROM queue_requests r JOIN queue q ON q.id=r.job_id LEFT JOIN download_preferences p ON p.user_id=r.user_id WHERE r.user_id=$user AND r.hidden=0 GROUP BY 1", ("$user", userId.ToString("N")));
        using var reader = await cmd.ExecuteReaderAsync(ct); var counts = new Dictionary<string, int>(); while (await reader.ReadAsync(ct)) counts[reader.GetString(0)] = reader.GetInt32(1); return counts;
    }
}
