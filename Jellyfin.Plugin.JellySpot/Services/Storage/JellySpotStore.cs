using System.Text.Json;
using Jellyfin.Plugin.JellySpot.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public class JellySpotStore
{
    private readonly ILogger<JellySpotStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _dbPath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public JellySpotStore(ILogger<JellySpotStore> logger)
    {
        _logger = logger;
        _dbPath = Plugin.Instance?.GetDataPath("jellyspot.db")
                  ?? Path.Combine(Path.GetTempPath(), "jellyspot.db");
        Initialize();
    }

    private string ConnectionString => $"Data Source={_dbPath}";

    private void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS user_tokens (
              jellyfin_user_id TEXT PRIMARY KEY,
              json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS user_settings (
              jellyfin_user_id TEXT PRIMARY KEY,
              json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS track_index (
              spotify_track_id TEXT PRIMARY KEY,
              youtube_video_id TEXT,
              match_score REAL,
              relative_path TEXT,
              title TEXT,
              artists TEXT,
              album TEXT,
              isrc TEXT,
              updated_at TEXT
            );
            CREATE TABLE IF NOT EXISTS queue (
              id TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              status TEXT,
              updated_at TEXT
            );
            CREATE TABLE IF NOT EXISTS playlist_snapshots (
              playlist_id TEXT NOT NULL,
              jellyfin_user_id TEXT NOT NULL,
              snapshot_id TEXT NOT NULL,
              name TEXT,
              updated_at TEXT,
              PRIMARY KEY (playlist_id, jellyfin_user_id)
            );
            CREATE TABLE IF NOT EXISTS oauth_state (
              state TEXT PRIMARY KEY,
              jellyfin_user_id TEXT NOT NULL,
              code_verifier TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS api_cache (
              cache_key TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              expires_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task SaveTokensAsync(Guid userId, SpotifyTokens tokens, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO user_tokens(jellyfin_user_id, json) VALUES($id, $json) ON CONFLICT(jellyfin_user_id) DO UPDATE SET json=excluded.json";
            cmd.Parameters.AddWithValue("$id", userId.ToString("N"));
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(tokens, _json));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SpotifyTokens?> GetTokensAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM user_tokens WHERE jellyfin_user_id=$n OR jellyfin_user_id=$d LIMIT 1";
        cmd.Parameters.AddWithValue("$n", userId.ToString("N"));
        cmd.Parameters.AddWithValue("$d", userId.ToString("D"));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string json ? JsonSerializer.Deserialize<SpotifyTokens>(json) : null;
    }

    public async Task DeleteTokensAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM user_tokens WHERE jellyfin_user_id=$id";
        cmd.Parameters.AddWithValue("$id", userId.ToString("N"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveUserSettingsAsync(UserSyncSettings settings, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO user_settings(jellyfin_user_id, json) VALUES($id, $json) ON CONFLICT(jellyfin_user_id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$id", settings.JellyfinUserId.ToString("N"));
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, _json));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<UserSyncSettings> GetUserSettingsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM user_settings WHERE jellyfin_user_id=$id";
        cmd.Parameters.AddWithValue("$id", userId.ToString("N"));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is string json)
        {
            return JsonSerializer.Deserialize<UserSyncSettings>(json) ?? new UserSyncSettings { JellyfinUserId = userId };
        }

        return new UserSyncSettings { JellyfinUserId = userId };
    }

    public async Task<IReadOnlyList<UserSyncSettings>> GetAllEnabledUsersAsync(CancellationToken ct = default)
    {
        var list = new List<UserSyncSettings>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM user_settings";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var settings = JsonSerializer.Deserialize<UserSyncSettings>(reader.GetString(0));
            if (settings is { Enabled: true })
            {
                list.Add(settings);
            }
        }

        return list;
    }

    public async Task UpsertTrackIndexAsync(TrackIndexEntry entry, CancellationToken ct = default)
    {
        entry.UpdatedAtUtc = DateTime.UtcNow;
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO track_index(spotify_track_id, youtube_video_id, match_score, relative_path, title, artists, album, isrc, updated_at)
            VALUES($id, $yt, $score, $path, $title, $artists, $album, $isrc, $updated)
            ON CONFLICT(spotify_track_id) DO UPDATE SET
              youtube_video_id=excluded.youtube_video_id,
              match_score=excluded.match_score,
              relative_path=excluded.relative_path,
              title=excluded.title,
              artists=excluded.artists,
              album=excluded.album,
              isrc=excluded.isrc,
              updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", entry.SpotifyTrackId);
        cmd.Parameters.AddWithValue("$yt", (object?)entry.YoutubeVideoId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$score", (object?)entry.MatchScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)entry.RelativePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", entry.Title);
        cmd.Parameters.AddWithValue("$artists", entry.Artists);
        cmd.Parameters.AddWithValue("$album", entry.Album);
        cmd.Parameters.AddWithValue("$isrc", (object?)entry.Isrc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", entry.UpdatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<TrackIndexEntry?> GetTrackIndexAsync(string spotifyTrackId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT spotify_track_id, youtube_video_id, match_score, relative_path, title, artists, album, isrc, updated_at FROM track_index WHERE spotify_track_id=$id";
        cmd.Parameters.AddWithValue("$id", spotifyTrackId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new TrackIndexEntry
        {
            SpotifyTrackId = reader.GetString(0),
            YoutubeVideoId = reader.IsDBNull(1) ? null : reader.GetString(1),
            MatchScore = reader.IsDBNull(2) ? null : reader.GetDouble(2),
            RelativePath = reader.IsDBNull(3) ? null : reader.GetString(3),
            Title = reader.GetString(4),
            Artists = reader.GetString(5),
            Album = reader.GetString(6),
            Isrc = reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    public async Task ClearTrackMatchAsync(string spotifyTrackId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE track_index SET youtube_video_id=NULL, match_score=NULL WHERE spotify_track_id=$id";
        cmd.Parameters.AddWithValue("$id", spotifyTrackId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(QueueItem item, CancellationToken ct = default)
    {
        item.UpdatedAtUtc = DateTime.UtcNow;
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using (var check = conn.CreateCommand())
        {
            check.CommandText =
                "SELECT COUNT(1) FROM queue WHERE json LIKE $like AND status IN ('Pending','Matching','Downloading')";
            check.Parameters.AddWithValue("$like", $"%\"SpotifyTrackId\":\"{item.SpotifyTrackId}\"%");
            var count = Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (count > 0)
            {
                return;
            }
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO queue(id, json, status, updated_at) VALUES($id, $json, $status, $updated)";
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, _json));
        cmd.Parameters.AddWithValue("$status", item.Status);
        cmd.Parameters.AddWithValue("$updated", item.UpdatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateQueueItemAsync(QueueItem item, CancellationToken ct = default)
    {
        item.UpdatedAtUtc = DateTime.UtcNow;
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE queue SET json=$json, status=$status, updated_at=$updated WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, _json));
        cmd.Parameters.AddWithValue("$status", item.Status);
        cmd.Parameters.AddWithValue("$updated", item.UpdatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<QueueItem?> GetQueueItemAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM queue WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string json ? JsonSerializer.Deserialize<QueueItem>(json) : null;
    }

    public async Task<IReadOnlyList<QueueItem>> GetQueueAsync(string? status = null, int limit = 200, CancellationToken ct = default)
    {
        var list = new List<QueueItem>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(status))
        {
            cmd.CommandText = "SELECT json FROM queue ORDER BY updated_at DESC LIMIT $limit";
        }
        else
        {
            cmd.CommandText = "SELECT json FROM queue WHERE status=$status ORDER BY updated_at DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$status", status);
        }

        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<QueueItem>(reader.GetString(0));
            if (item != null)
            {
                list.Add(item);
            }
        }

        return list;
    }

    public async Task SavePlaylistSnapshotAsync(PlaylistSnapshot snapshot, CancellationToken ct = default)
    {
        snapshot.UpdatedAtUtc = DateTime.UtcNow;
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO playlist_snapshots(playlist_id, jellyfin_user_id, snapshot_id, name, updated_at)
            VALUES($pid, $uid, $sid, $name, $updated)
            ON CONFLICT(playlist_id, jellyfin_user_id) DO UPDATE SET
              snapshot_id=excluded.snapshot_id,
              name=excluded.name,
              updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$pid", snapshot.PlaylistId);
        cmd.Parameters.AddWithValue("$uid", snapshot.JellyfinUserId.ToString("N"));
        cmd.Parameters.AddWithValue("$sid", snapshot.SnapshotId);
        cmd.Parameters.AddWithValue("$name", snapshot.Name);
        cmd.Parameters.AddWithValue("$updated", snapshot.UpdatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<PlaylistSnapshot?> GetPlaylistSnapshotAsync(Guid userId, string playlistId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT playlist_id, jellyfin_user_id, snapshot_id, name, updated_at FROM playlist_snapshots WHERE playlist_id=$pid AND jellyfin_user_id=$uid";
        cmd.Parameters.AddWithValue("$pid", playlistId);
        cmd.Parameters.AddWithValue("$uid", userId.ToString("N"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new PlaylistSnapshot
        {
            PlaylistId = reader.GetString(0),
            JellyfinUserId = Guid.Parse(reader.GetString(1)),
            SnapshotId = reader.GetString(2),
            Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    public async Task SaveOAuthStateAsync(string state, Guid userId, string codeVerifier, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO oauth_state(state, jellyfin_user_id, code_verifier, created_at) VALUES($state, $uid, $verifier, $created)";
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$uid", userId.ToString("N"));
        cmd.Parameters.AddWithValue("$verifier", codeVerifier);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<(Guid UserId, string CodeVerifier)?> TakeOAuthStateAsync(string state, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT jellyfin_user_id, code_verifier FROM oauth_state WHERE state=$state";
        cmd.Parameters.AddWithValue("$state", state);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var userId = Guid.Parse(reader.GetString(0));
        var verifier = reader.GetString(1);
        await reader.CloseAsync().ConfigureAwait(false);

        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM oauth_state WHERE state=$state";
        del.Parameters.AddWithValue("$state", state);
        await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return (userId, verifier);
    }

    public async Task SetCacheAsync(string key, string json, TimeSpan ttl, CancellationToken ct = default)
    {
        var expires = DateTime.UtcNow.Add(ttl).ToString("O");
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO api_cache(cache_key, json, expires_at) VALUES($key, $json, $exp) ON CONFLICT(cache_key) DO UPDATE SET json=excluded.json, expires_at=excluded.expires_at";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$exp", expires);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetCacheAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json, expires_at FROM api_cache WHERE cache_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var expires = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
        if (expires < DateTime.UtcNow)
        {
            return null;
        }

        return reader.GetString(0);
    }
}
