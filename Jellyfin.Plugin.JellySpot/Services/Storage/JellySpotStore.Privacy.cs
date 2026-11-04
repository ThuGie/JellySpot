using System.Text.Json;
using Jellyfin.Plugin.JellySpot.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public partial class JellySpotStore
{
    private void MigratePrivacy(SqliteConnection conn)
    {
        using var version = conn.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(version.ExecuteScalar()) < 1)
        {
            using var backup = new SqliteConnection($"Data Source={_dbPath}.pre-privacy.bak");
            backup.Open();
            conn.BackupDatabase(backup);
            using var tx = conn.BeginTransaction();
            using var schema = conn.CreateCommand();
            schema.Transaction = tx;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS queue_requests (
                  job_id TEXT NOT NULL, user_id TEXT NOT NULL, source_id TEXT NOT NULL, source_name TEXT NOT NULL,
                  PRIMARY KEY(job_id,user_id,source_id));
                CREATE INDEX IF NOT EXISTS ix_requests_user ON queue_requests(user_id,job_id);
                CREATE TABLE IF NOT EXISTS collections (
                  id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, source_id TEXT NOT NULL,
                  shared INTEGER NOT NULL DEFAULT 0, json TEXT NOT NULL, UNIQUE(owner_id,source_id));
                CREATE TABLE IF NOT EXISTS collection_follows (
                  collection_id TEXT NOT NULL, user_id TEXT NOT NULL, PRIMARY KEY(collection_id,user_id));
                """;
            schema.ExecuteNonQuery();
            // All migration commands on this connection enlist in the transaction.
            MigrateQueueTrackId(conn, tx);
            foreach (var table in new[] { "user_tokens", "user_settings" })
            {
                using var normalize = conn.CreateCommand();
                normalize.Transaction = tx;
                normalize.CommandText = $"INSERT OR IGNORE INTO {table}(jellyfin_user_id,json) SELECT lower(replace(jellyfin_user_id,'-','')),json FROM {table}; DELETE FROM {table} WHERE jellyfin_user_id != lower(replace(jellyfin_user_id,'-',''));";
                normalize.ExecuteNonQuery();
            }
            using var seed = conn.CreateCommand();
            seed.Transaction = tx;
            seed.CommandText = """
                INSERT OR IGNORE INTO queue_requests(job_id,user_id,source_id,source_name)
                SELECT id, lower(replace(json_extract(json,'$.JellyfinUserId'),'-','')),
                  coalesce(json_extract(json,'$.PlaylistId'),''),coalesce(json_extract(json,'$.PlaylistName'),'') FROM queue
                WHERE json_extract(json,'$.JellyfinUserId') IS NOT NULL;
                INSERT OR IGNORE INTO collections(id,owner_id,source_id,json)
                SELECT lower(hex(randomblob(16))),jellyfin_user_id,playlist_id,
                  json_object('SourceId',playlist_id,'Name',coalesce(name,'Playlist'))
                FROM playlist_snapshots;
                INSERT OR IGNORE INTO collections(id,owner_id,source_id,json)
                SELECT lower(hex(randomblob(16))), s.jellyfin_user_id, p.value,
                  json_object('SourceId',p.value,'Name','Playlist')
                FROM user_settings s, json_each(s.json,'$.MonitoredPlaylistIds') p;
                INSERT OR IGNORE INTO collections(id,owner_id,source_id,json)
                SELECT lower(hex(randomblob(16))), jellyfin_user_id,'liked',json_object('SourceId','liked','Name','Liked Songs')
                FROM user_settings WHERE json_extract(json,'$.SyncLikedSongs')=1;
                DELETE FROM queue_requests WHERE job_id NOT IN (SELECT id FROM queue);
                UPDATE user_tokens SET json=json_set(json,'$.Generation',lower(hex(randomblob(16))))
                WHERE json_extract(json,'$.Generation') IS NULL;
                PRAGMA user_version=1;
                """;
            seed.ExecuteNonQuery();
            tx.Commit();
        }
        // A terminated process must not leave work permanently active.
        using var recover = conn.CreateCommand();
        recover.CommandText = "UPDATE queue SET status='Pending',json=json_set(json,'$.Status','Pending') WHERE status IN ('Matching','Downloading')";
        recover.ExecuteNonQuery();
    }

    public async Task<Guid?> GetOAuthStateUserAsync(string state, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT jellyfin_user_id FROM oauth_state WHERE state=$state";
        cmd.Parameters.AddWithValue("$state", state);
        return await cmd.ExecuteScalarAsync(ct) is string value ? Guid.Parse(value) : null;
    }

    public async Task RemoveMissingOwnersAsync(Func<Guid, bool> exists, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var missing = new List<string>();
        using (var list = conn.CreateCommand())
        {
            list.CommandText = "SELECT DISTINCT owner_id FROM collections";
            using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) if (!exists(Guid.Parse(reader.GetString(0)))) missing.Add(reader.GetString(0));
        }
        foreach (var owner in missing)
        {
            using var delete = conn.CreateCommand();
            delete.CommandText = "DELETE FROM collection_follows WHERE collection_id IN (SELECT id FROM collections WHERE owner_id=$owner); DELETE FROM collections WHERE owner_id=$owner";
            delete.Parameters.AddWithValue("$owner", owner);
            await delete.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task ClearUserCacheAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM api_cache WHERE cache_key LIKE $prefix; DELETE FROM oauth_state WHERE jellyfin_user_id=$user";
        cmd.Parameters.AddWithValue("$prefix", userId.ToString("N") + ":%");
        cmd.Parameters.AddWithValue("$user", userId.ToString("N"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<QueueItem>> GetUserQueueAsync(Guid userId, string? status, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT q.json, min(r.source_name) FROM queue q JOIN queue_requests r ON r.job_id=q.id WHERE r.user_id=$user AND ($status IS NULL OR q.status=$status) GROUP BY q.id ORDER BY q.updated_at DESC LIMIT 300";
        cmd.Parameters.AddWithValue("$user", userId.ToString("N"));
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<QueueItem>();
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize<QueueItem>(reader.GetString(0))!;
            item.JellyfinUserId = userId;
            item.PlaylistId = null;
            item.PlaylistName = reader.GetString(1);
            item.RelativePath = null;
            item.Error = item.Status == "Failed" ? "Download failed. Retry or contact your administrator." : null;
            result.Add(item);
        }
        return result;
    }

    public async Task<bool> RetryJobAsync(string id, Guid? userId, bool rematch = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct);
            using var tx = conn.BeginTransaction();
            using var find = conn.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT json FROM queue WHERE id=$id AND ($user IS NULL OR EXISTS(SELECT 1 FROM queue_requests r WHERE r.job_id=queue.id AND r.user_id=$user))";
            find.Parameters.AddWithValue("$id", id);
            find.Parameters.AddWithValue("$user", userId.HasValue ? userId.Value.ToString("N") : DBNull.Value);
            var json = await find.ExecuteScalarAsync(ct) as string;
            if (json == null) return false;
            var job = JsonSerializer.Deserialize<QueueItem>(json)!;
            if (job.Status != "Failed" && !(rematch && job.Status == "Completed") && !(userId == null && job.Status == "Pending")) return true;
            job.AdminOverride = userId == null;
            job.Status = "Pending";
            job.Error = null;
            job.UpdatedAtUtc = DateTime.UtcNow;
            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE queue SET json=$json,status='Pending',updated_at=$updated WHERE id=$id";
            if (rematch)
            {
                job.RematchRequested = true;
                job.YoutubeVideoId = null;
                job.MatchScore = null;
                update.CommandText += "; UPDATE track_index SET youtube_video_id=NULL,match_score=NULL WHERE spotify_track_id=$track";
                update.Parameters.AddWithValue("$track", job.SpotifyTrackId);
            }
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(job));
            update.Parameters.AddWithValue("$updated", job.UpdatedAtUtc.ToString("O"));
            await update.ExecuteNonQueryAsync(ct);
            tx.Commit();
            return true;
        }
        finally { _lock.Release(); }
    }

    private static UserCollection ReadCollection(SqliteDataReader reader)
    {
        var value = JsonSerializer.Deserialize<UserCollection>(reader.GetString(3))!;
        value.Id = reader.GetString(0);
        value.OwnerId = Guid.Parse(reader.GetString(1));
        value.Shared = reader.GetBoolean(2);
        return value;
    }

    public async Task<List<UserCollection>> GetCollectionsAsync(Guid userId, string view = "mine", CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,owner_id,shared,json FROM collections c WHERE " + (view switch
        {
            "shared" => "shared=1",
            "following" => "shared=1 AND EXISTS(SELECT 1 FROM collection_follows f WHERE f.collection_id=c.id AND f.user_id=$user)",
            _ => "owner_id=$user"
        });
        cmd.Parameters.AddWithValue("$user", userId.ToString("N"));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<UserCollection>();
        while (await reader.ReadAsync(ct)) result.Add(ReadCollection(reader));
        return result;
    }

    public async Task<UserCollection?> GetCollectionAsync(string id, Guid userId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,owner_id,shared,json FROM collections WHERE id=$id AND (owner_id=$user OR shared=1)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$user", userId.ToString("N"));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCollection(reader) : null;
    }

    public Task SaveCollectionAsync(UserCollection collection, CancellationToken ct = default)
        => SaveBrowserCollectionAsync(collection, ct);

    public async Task<bool> SetSharingAsync(string id, Guid userId, bool shared, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE collections SET shared_at=CASE WHEN $shared=1 AND shared=0 THEN $now ELSE shared_at END,shared=$shared WHERE id=$id AND owner_id=$user";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$user", userId.ToString("N"));
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$shared", shared);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> FollowCollectionAsync(string id, Guid userId, bool follow, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.CommandText = follow
            ? "INSERT OR IGNORE INTO collection_follows(collection_id,user_id,followed_at) SELECT id,$user,$now FROM collections WHERE id=$id AND shared=1 AND owner_id!=$user"
            : "DELETE FROM collection_follows WHERE collection_id=$id AND user_id=$user";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$user", userId.ToString("N"));
        await cmd.ExecuteNonQueryAsync(ct);
        return !follow || await GetCollectionAsync(id, userId, ct) != null;
    }

    public async Task<bool> IsFollowingAsync(string id, Guid userId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM collection_follows WHERE collection_id=$id AND user_id=$user";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$user", userId.ToString("N"));
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    public async Task<bool> DeleteCollectionAsync(string id, Guid userId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM browser_entries WHERE scope=$id AND EXISTS(SELECT 1 FROM collections WHERE id=$id AND owner_id=$user); DELETE FROM collection_follows WHERE collection_id=$id AND EXISTS(SELECT 1 FROM collections WHERE id=$id AND owner_id=$user); DELETE FROM collections WHERE id=$id AND owner_id=$user";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$user", userId.ToString("N"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
