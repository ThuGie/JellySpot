using System.Text.Json;
using Jellyfin.Plugin.JellySpot.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public partial class JellySpotStore
{
    private static void WriteEntries(SqliteConnection c, SqliteTransaction tx, string scope, IEnumerable<SpotifyTrackInfo> tracks)
    {
        using var delete = Command(c, "DELETE FROM browser_entries WHERE scope=$scope", ("$scope", scope)); delete.Transaction = tx; delete.ExecuteNonQuery();
        using var insert = Command(c, "INSERT INTO browser_entries(scope,entry_id,position,track_id,title,artist,album,duration,added_at,json) VALUES($scope,$entry,$position,$track,$title,$artist,$album,$duration,$added,$json)"); insert.Transaction = tx;
        foreach (var key in new[] { "$scope", "$entry", "$position", "$track", "$title", "$artist", "$album", "$duration", "$added", "$json" }) insert.Parameters.Add(new SqliteParameter(key, DBNull.Value));
        var position = 0; var occurrences = new Dictionary<string, int>();
        foreach (var track in tracks)
        {
            insert.Parameters["$scope"].Value = scope; var identity = track.Id + "|" + track.AddedAtUtc?.ToString("O"); occurrences.TryGetValue(identity, out var occurrence); occurrences[identity] = occurrence + 1;
            insert.Parameters["$entry"].Value = Revision(identity + "|" + occurrence)[..32]; insert.Parameters["$position"].Value = position++;
            insert.Parameters["$track"].Value = track.Id; insert.Parameters["$title"].Value = track.Name; insert.Parameters["$artist"].Value = string.Join(", ", track.Artists); insert.Parameters["$album"].Value = track.Album;
            insert.Parameters["$duration"].Value = track.DurationMs; insert.Parameters["$added"].Value = (object?)track.AddedAtUtc?.ToString("O") ?? DBNull.Value; insert.Parameters["$json"].Value = JsonSerializer.Serialize(track); insert.ExecuteNonQuery();
        }
    }
    private async Task SaveBrowserCollectionAsync(UserCollection collection, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var c = await OpenBrowserAsync(ct); using var tx = c.BeginTransaction();
            using var find = Command(c, "SELECT id FROM collections WHERE owner_id=$owner AND source_id=$source", ("$owner", collection.OwnerId.ToString("N")), ("$source", collection.SourceId)); find.Transaction = tx;
            collection.Id = await find.ExecuteScalarAsync(ct) as string ?? collection.Id;
            using var save = Command(c, "INSERT INTO collections(id,owner_id,source_id,shared,json,imported_at,track_count) VALUES($id,$owner,$source,0,$json,$now,$count) ON CONFLICT(owner_id,source_id) DO UPDATE SET json=excluded.json,track_count=excluded.track_count", ("$id", collection.Id), ("$owner", collection.OwnerId.ToString("N")), ("$source", collection.SourceId), ("$json", JsonSerializer.Serialize(collection)), ("$now", DateTime.UtcNow.ToString("O")), ("$count", collection.Tracks.Count)); save.Transaction = tx; await save.ExecuteNonQueryAsync(ct);
            WriteEntries(c, tx, collection.Id, collection.Tracks); tx.Commit();
        }
        finally { _lock.Release(); }
    }
    public async Task<BrowserPage<CollectionSummary>> CollectionPageAsync(Guid userId, string view, PageQuery query, Func<Guid, string?> ownerName, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct);
        var owners = new Dictionary<string, string?>();
        c.CreateFunction<string, string?>("owner_name", id => { if (!owners.TryGetValue(id, out var name)) owners[id] = name = ownerName(Guid.Parse(id)); return name; });
        var access = view switch { "shared" => "c.shared=1", "following" => "c.shared=1 AND f.user_id IS NOT NULL", _ => "c.owner_id=$user" };
        var where = $"{access} AND owner_name(c.owner_id) IS NOT NULL AND ($q='' OR json_extract(c.json,'$.Name') LIKE $q) AND ($filter='' OR ($filter='shared' AND c.shared=1) OR ($filter='private' AND c.shared=0))";
        var from = " FROM collections c LEFT JOIN collection_follows f ON f.collection_id=c.id AND f.user_id=$user ";
        var args = new (string, object?)[] { ("$user", userId.ToString("N")), ("$q", string.IsNullOrWhiteSpace(query.Q) ? "" : "%" + query.Q + "%"), ("$filter", query.Filter) };
        using var count = Command(c, "SELECT count(*)" + from + " WHERE " + where, args); var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        var sort = query.Sort switch { "imported" => "c.imported_at", "shared" => "c.shared_at", "followed" => "f.followed_at", "updated" => "json_extract(c.json,'$.UpdatedAtUtc')", "owner" => "owner_name(c.owner_id) COLLATE NOCASE", _ => "json_extract(c.json,'$.Name') COLLATE NOCASE" };
        using var cmd = Command(c, "SELECT c.id,c.source_id,c.owner_id,c.shared,c.track_count,c.imported_at,c.shared_at,f.followed_at,json_extract(c.json,'$.Name'),json_extract(c.json,'$.CoverUrl'),json_extract(c.json,'$.UpdatedAtUtc'),owner_name(c.owner_id),f.user_id" + from + $"WHERE {where} ORDER BY ({sort} IS NULL),{sort} " + (query.Direction == "desc" ? "DESC" : "ASC") + ",c.id LIMIT $limit OFFSET $offset", args.Concat(new[] { ("$limit", (object?)query.Size), ("$offset", (object?)query.Offset) }).ToArray());
        using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<CollectionSummary>();
        string? Nullable(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (await reader.ReadAsync(ct)) rows.Add(new() { Id = reader.GetString(0), SourceId = reader.GetString(1), Owned = reader.GetString(2) == userId.ToString("N"), Shared = reader.GetBoolean(3), TrackCount = reader.GetInt32(4), ImportedAtUtc = Nullable(5), SharedAtUtc = Nullable(6), FollowedAtUtc = Nullable(7), Following = !reader.IsDBNull(12), Name = Nullable(8) ?? "Playlist", CoverUrl = Nullable(9), UpdatedAtUtc = Nullable(10), OwnerName = Nullable(11) });
        var settings = await GetUserSettingsAsync(userId, ct);
        foreach (var row in rows) row.Monitored = row.Owned && (row.SourceId == "liked" ? settings.SyncLikedSongs : settings.MonitoredPlaylistIds.Contains(row.SourceId));
        return new(rows, total, Math.Max(1, query.Page), query.Size, Revision(new { rows, total }));
    }
    public static string CatalogScope(Guid userId, string generation, string kind) => $"catalog:{userId:N}:{generation}:{kind}";
    public async Task SaveCatalogAsync(Guid userId, string generation, string kind, IEnumerable<object> items, IEnumerable<SpotifyTrackInfo>? tracks = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var c = await OpenBrowserAsync(ct); using var tx = c.BeginTransaction();
            using var delete = Command(c, "DELETE FROM browser_catalog WHERE user_id=$user AND generation=$gen AND kind=$kind", ("$user", userId.ToString("N")), ("$gen", generation), ("$kind", kind)); delete.Transaction = tx; await delete.ExecuteNonQueryAsync(ct);
            foreach (var value in items)
            {
                var node = JsonSerializer.SerializeToElement(value); var id = node.GetProperty("Id").GetString()!; var name = node.GetProperty("Name").GetString() ?? "";
                var added = node.TryGetProperty("AddedAtUtc", out var date) && date.ValueKind == JsonValueKind.String ? date.GetString() : null;
                using var cmd = Command(c, "INSERT OR REPLACE INTO browser_catalog(user_id,generation,kind,id,name,added_at,json) VALUES($user,$gen,$kind,$id,$name,$added,$json)", ("$user", userId.ToString("N")), ("$gen", generation), ("$kind", kind), ("$id", id), ("$name", name), ("$added", added), ("$json", node.GetRawText())); cmd.Transaction = tx; await cmd.ExecuteNonQueryAsync(ct);
            }
            if (tracks != null) WriteEntries(c, tx, CatalogScope(userId, generation, kind), tracks);
            using var complete = Command(c, "INSERT INTO browser_snapshots(user_id,generation,kind,complete,updated_at) VALUES($user,$gen,$kind,1,$now) ON CONFLICT(user_id,generation,kind) DO UPDATE SET complete=1,updated_at=excluded.updated_at", ("$user", userId.ToString("N")), ("$gen", generation), ("$kind", kind), ("$now", DateTime.UtcNow.ToString("O"))); complete.Transaction = tx; await complete.ExecuteNonQueryAsync(ct); tx.Commit();
        }
        finally { _lock.Release(); }
    }
    public async Task<BrowserPage<JsonElement>> CatalogPageAsync(Guid userId, string generation, string kind, PageQuery query, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); var args = new (string, object?)[] { ("$user", userId.ToString("N")), ("$gen", generation), ("$kind", kind), ("$q", string.IsNullOrEmpty(query.Q) ? "" : "%" + query.Q + "%") };
        var where = "user_id=$user AND generation=$gen AND kind=$kind AND ($q='' OR name LIKE $q)";
        using var count = Command(c, "SELECT count(*) FROM browser_catalog WHERE " + where, args); var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        var sort = query.Sort == "added" ? "added_at" : "name COLLATE NOCASE";
        using var cmd = Command(c, $"SELECT json FROM browser_catalog WHERE {where} ORDER BY ({sort} IS NULL),{sort} " + (query.Direction == "desc" ? "DESC" : "ASC") + ",id LIMIT $limit OFFSET $offset", args.Concat(new[] { ("$limit", (object?)query.Size), ("$offset", (object?)query.Offset) }).ToArray());
        using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<JsonElement>(); while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        using var ready = Command(c, "SELECT complete,updated_at FROM browser_snapshots WHERE user_id=$user AND generation=$gen AND kind=$kind", args.Take(3).ToArray()); using var snapshot = await ready.ExecuteReaderAsync(ct); var exists = await snapshot.ReadAsync(ct); var complete = exists && snapshot.GetBoolean(0); var updated = exists ? snapshot.GetString(1) : null;
        return new(rows, total, Math.Max(1, query.Page), query.Size, Revision(new { rows, total, complete, updated }), complete);
    }
    public async Task<List<BrowserTrack>> ScopeTracksAsync(string scope, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); using var cmd = Command(c, "SELECT entry_id,position,json FROM browser_entries WHERE scope=$scope ORDER BY position", ("$scope", scope)); using var reader = await cmd.ExecuteReaderAsync(ct); var tracks = new List<BrowserTrack>();
        while (await reader.ReadAsync(ct)) tracks.Add(new() { EntryId = reader.GetString(0), Position = reader.GetInt32(1), Track = JsonSerializer.Deserialize<SpotifyTrackInfo>(reader.GetString(2))! }); return tracks;
    }
    public async Task<BrowserPage<BrowserTrack>> TrackPageAsync(string scope, PageQuery query, IReadOnlyDictionary<string, string> available, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); var where = "scope=$scope AND ($q='' OR title LIKE $q OR artist LIKE $q OR album LIKE $q) AND ($filter='' OR ($filter='available' AND track_id IN (SELECT value FROM json_each($available))) OR ($filter='missing' AND track_id NOT IN (SELECT value FROM json_each($available))))";
        var args = new (string, object?)[] { ("$scope", scope), ("$q", string.IsNullOrWhiteSpace(query.Q) ? "" : "%" + query.Q + "%"), ("$filter", query.Filter), ("$available", JsonSerializer.Serialize(available.Keys)) };
        using var count = Command(c, "SELECT count(*) FROM browser_entries WHERE " + where, args); var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        var sort = query.Sort switch { "title" => "title COLLATE NOCASE", "artist" => "artist COLLATE NOCASE", "album" => "album COLLATE NOCASE", "duration" => "duration", "added" => "added_at", _ => "position" };
        using var cmd = Command(c, $"SELECT entry_id,position,json FROM browser_entries WHERE {where} ORDER BY ({sort} IS NULL),{sort} " + (query.Direction == "desc" ? "DESC" : "ASC") + ",position,entry_id LIMIT $limit OFFSET $offset", args.Concat(new[] { ("$limit", (object?)query.Size), ("$offset", (object?)query.Offset) }).ToArray()); using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<BrowserTrack>();
        while (await reader.ReadAsync(ct)) { var track = JsonSerializer.Deserialize<SpotifyTrackInfo>(reader.GetString(2))!; available.TryGetValue(track.Id, out var itemId); rows.Add(new() { EntryId = reader.GetString(0), Position = reader.GetInt32(1), Track = track, ItemId = itemId, Availability = itemId == null ? "Unavailable" : "Available" }); }
        return new(rows, total, Math.Max(1, query.Page), query.Size, Revision(new { rows, total }));
    }
    public async Task<Dictionary<string, TrackIndexEntry>> TrackIndexesAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); using var cmd = Command(c, "SELECT spotify_track_id,relative_path FROM track_index WHERE spotify_track_id IN (SELECT value FROM json_each($ids))", ("$ids", JsonSerializer.Serialize(ids.Distinct()))); using var reader = await cmd.ExecuteReaderAsync(ct); var result = new Dictionary<string, TrackIndexEntry>();
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = new() { SpotifyTrackId = reader.GetString(0), RelativePath = reader.IsDBNull(1) ? null : reader.GetString(1) }; return result;
    }
    public async Task<SpotifyTrackInfo?> KnownTrackAsync(Guid userId, string generation, string id, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); using var cmd = Command(c, "SELECT json FROM browser_entries WHERE track_id=$id AND (scope LIKE $prefix OR scope IN (SELECT id FROM collections WHERE owner_id=$user)) LIMIT 1", ("$id", id), ("$prefix", $"catalog:{userId:N}:{generation}:%"), ("$user", userId.ToString("N")));
        return await cmd.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<SpotifyTrackInfo>(json) : null;
    }
    public async Task<UserCollection?> CollectionMetadataAsync(string id, Guid userId, CancellationToken ct = default)
    {
        await using var c = await OpenBrowserAsync(ct); using var cmd = Command(c, "SELECT id,owner_id,source_id,shared,json_extract(json,'$.Name'),json_extract(json,'$.CoverUrl'),json_extract(json,'$.UpdatedAtUtc') FROM collections WHERE id=$id AND (owner_id=$user OR shared=1)", ("$id", id), ("$user", userId.ToString("N"))); using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new() { Id = reader.GetString(0), OwnerId = Guid.Parse(reader.GetString(1)), SourceId = reader.GetString(2), Shared = reader.GetBoolean(3), Name = reader.IsDBNull(4) ? "Playlist" : reader.GetString(4), CoverUrl = reader.IsDBNull(5) ? null : reader.GetString(5), UpdatedAtUtc = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind) };
    }
}
