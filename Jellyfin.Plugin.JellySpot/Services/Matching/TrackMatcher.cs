using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Jellyfin.Plugin.JellySpot.Services.YouTubeMusic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Matching;

public class TrackMatcher
{
    private readonly YouTubeMusicClient _ytm;
    private readonly MatchScorer _scorer;
    private readonly JellySpotStore _store;
    private readonly ILogger<TrackMatcher> _logger;

    public TrackMatcher(
        YouTubeMusicClient ytm,
        MatchScorer scorer,
        JellySpotStore store,
        ILogger<TrackMatcher> logger)
    {
        _ytm = ytm;
        _scorer = scorer;
        _store = store;
        _logger = logger;
    }

    public async Task<(MatchCandidate? Candidate, double Score)> MatchAsync(
        SpotifyTrackInfo song,
        bool forceRematch = false,
        CancellationToken ct = default)
    {
        var minScore = Plugin.Instance?.Configuration.MinMatchScore ?? 80.0;

        if (!forceRematch)
        {
            var existing = await _store.GetTrackIndexAsync(song.Id, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existing?.YoutubeVideoId))
            {
                return (new MatchCandidate
                {
                    VideoId = existing.YoutubeVideoId!,
                    Url = $"https://music.youtube.com/watch?v={existing.YoutubeVideoId}",
                    Title = existing.Title,
                    Author = existing.Artists.Split(',').FirstOrDefault()?.Trim() ?? string.Empty,
                    Verified = true
                }, existing.MatchScore ?? 100);
            }
        }

        if (!string.IsNullOrWhiteSpace(song.Isrc))
        {
            var isrcResults = await _ytm.SearchAsync(song.Isrc, "songs", ct).ConfigureAwait(false);
            foreach (var r in isrcResults)
            {
                r.IsrcSearch = true;
            }

            if (isrcResults.Count == 1 && isrcResults[0].Verified)
            {
                await PersistMatchAsync(song, isrcResults[0], 100, ct).ConfigureAwait(false);
                return (isrcResults[0], 100);
            }

            if (isrcResults.Count > 0)
            {
                var scored = _scorer.OrderResults(isrcResults, song);
                var best = scored.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key.Views).FirstOrDefault();
                if (best.Key != null && best.Value > minScore)
                {
                    await PersistMatchAsync(song, best.Key, best.Value, ct).ConfigureAwait(false);
                    return (best.Key, best.Value);
                }
            }
        }

        var query = CreateSongTitle(song.Name, song.Artists);
        var songResults = await _ytm.SearchAsync(query, "songs", ct).ConfigureAwait(false);
        var videoResults = await _ytm.SearchAsync(query, "videos", ct).ConfigureAwait(false);
        var all = songResults.Concat(videoResults).ToList();
        if (all.Count == 0)
        {
            _logger.LogWarning("No YouTube Music results for {Title}", query);
            return (null, 0);
        }

        var ordered = _scorer.OrderResults(all, song);
        var winner = ordered.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key.Views).FirstOrDefault();
        if (winner.Key == null || winner.Value < minScore)
        {
            _logger.LogWarning(
                "Best match for {Title} scored {Score}, below threshold {Threshold}",
                query,
                winner.Value,
                minScore);
            return (winner.Key, winner.Value);
        }

        await PersistMatchAsync(song, winner.Key, winner.Value, ct).ConfigureAwait(false);
        return (winner.Key, winner.Value);
    }

    private async Task PersistMatchAsync(SpotifyTrackInfo song, MatchCandidate candidate, double score, CancellationToken ct)
    {
        await _store.UpsertTrackIndexAsync(new TrackIndexEntry
        {
            SpotifyTrackId = song.Id,
            YoutubeVideoId = candidate.VideoId,
            MatchScore = score,
            Title = song.Name,
            Artists = string.Join(", ", song.Artists),
            Album = song.Album,
            Isrc = song.Isrc
        }, ct).ConfigureAwait(false);
    }

    private static string CreateSongTitle(string name, IReadOnlyList<string> artists)
    {
        if (artists.Count == 0)
        {
            return name;
        }

        return $"{artists[0]} - {name}";
    }
}
