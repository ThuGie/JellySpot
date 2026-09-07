using Jellyfin.Plugin.JellySpot.Services.Storage;

namespace Jellyfin.Plugin.JellySpot.Services.Spotify;

public class SpotifyCache
{
    private readonly JellySpotStore _store;

    public SpotifyCache(JellySpotStore store)
    {
        _store = store;
    }

    public Task SetAsync(string key, string json, CancellationToken ct = default)
    {
        var days = Math.Max(1, Plugin.Instance?.Configuration.CacheTtlDays ?? 10);
        return _store.SetCacheAsync(key, json, TimeSpan.FromDays(days), ct);
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        return _store.GetCacheAsync(key, ct);
    }
}
