namespace Jellyfin.Plugin.JellySpot.Models;

public class SpotifyTokens
{
    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string SpotifyUserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}

public class UserSyncSettings
{
    public Guid JellyfinUserId { get; set; }

    public bool Enabled { get; set; } = true;

    public bool SyncLikedSongs { get; set; }

    public List<string> MonitoredPlaylistIds { get; set; } = [];

    public List<string> MonitoredAlbumIds { get; set; } = [];

    public List<string> IncludeArtistFilters { get; set; } = [];

    public List<string> ExcludeArtistFilters { get; set; } = [];

    public DateTime? LastSyncUtc { get; set; }

    public string? LastSyncStatus { get; set; }

    public string? LastError { get; set; }
}

public class TrackIndexEntry
{
    public string SpotifyTrackId { get; set; } = string.Empty;

    public string? YoutubeVideoId { get; set; }

    public double? MatchScore { get; set; }

    public string? RelativePath { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Artists { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public string? Isrc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public class QueueItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public Guid JellyfinUserId { get; set; }

    public string SpotifyTrackId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artists { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public string? Isrc { get; set; }

    public int DurationMs { get; set; }

    public int? TrackNumber { get; set; }

    public int? DiscNumber { get; set; }

    public string? AlbumArtist { get; set; }

    public int? Year { get; set; }

    public string? CoverUrl { get; set; }

    public string? PlaylistId { get; set; }

    public string? PlaylistName { get; set; }

    public string Status { get; set; } = "Pending";

    public string? YoutubeVideoId { get; set; }

    public double? MatchScore { get; set; }

    public string? Error { get; set; }

    public string? RelativePath { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum EnqueueResult
{
    Added,
    AlreadyQueued,
    AlreadyDownloaded,
    Retried
}

public class PlaylistSnapshot
{
    public string PlaylistId { get; set; } = string.Empty;

    public Guid JellyfinUserId { get; set; }

    public string SnapshotId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}

public class SpotifyTrackInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Artists { get; set; } = [];

    public string Album { get; set; } = string.Empty;

    public string? AlbumArtist { get; set; }

    public string? Isrc { get; set; }

    public int DurationMs { get; set; }

    public int TrackNumber { get; set; }

    public int DiscNumber { get; set; } = 1;

    public int? Year { get; set; }

    public string? CoverUrl { get; set; }

    public string? AlbumId { get; set; }
}

public class SpotifyPlaylistInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? ImageUrl { get; set; }

    public int TrackCount { get; set; }

    public string? SnapshotId { get; set; }

    public bool Collaborative { get; set; }

    public string? OwnerId { get; set; }
}

public class SpotifyAlbumInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Artists { get; set; } = [];

    public string? ImageUrl { get; set; }

    public int TrackCount { get; set; }

    public int? Year { get; set; }

    public string? AlbumType { get; set; }
}

public class SpotifyArtistInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ImageUrl { get; set; }

    public List<string> Genres { get; set; } = [];

    public int Followers { get; set; }
}

public class MatchCandidate
{
    public string VideoId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public List<string> Artists { get; set; } = [];

    public string? Album { get; set; }

    public double DurationSeconds { get; set; }

    public long Views { get; set; }

    public bool Verified { get; set; }

    public bool IsrcSearch { get; set; }

    public string SearchQuery { get; set; } = string.Empty;
}
