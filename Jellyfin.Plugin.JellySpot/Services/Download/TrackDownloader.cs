using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services;
using Jellyfin.Plugin.JellySpot.Services.Matching;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;

namespace Jellyfin.Plugin.JellySpot.Services.Download;

public class TrackDownloader
{
    private readonly TrackMatcher _matcher;
    private readonly LibraryStorage _storage;
    private readonly JellySpotStore _store;
    private readonly FfmpegLocator _ffmpeg;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TrackDownloader> _logger;
    private readonly YoutubeClient _youtube = new();

    public TrackDownloader(
        TrackMatcher matcher,
        LibraryStorage storage,
        JellySpotStore store,
        FfmpegLocator ffmpeg,
        IHttpClientFactory httpClientFactory,
        ILogger<TrackDownloader> logger)
    {
        _matcher = matcher;
        _storage = storage;
        _store = store;
        _ffmpeg = ffmpeg;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task DownloadAsync(QueueItem item, bool forceRematch = false, CancellationToken ct = default)
    {
        var track = ToTrackInfo(item);

        if (!forceRematch && await _storage.TrackFileExistsAsync(track.Id, ct).ConfigureAwait(false))
        {
            var existing = await _store.GetTrackIndexAsync(track.Id, ct).ConfigureAwait(false);
            item.Status = "Completed";
            item.RelativePath = existing?.RelativePath;
            item.YoutubeVideoId = existing?.YoutubeVideoId;
            item.MatchScore = existing?.MatchScore;
            item.Error = null;
            await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
            return;
        }

        item.Status = "Matching";
        await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);

        var (candidate, score) = await _matcher.MatchAsync(track, forceRematch, ct).ConfigureAwait(false);
        item.MatchScore = score;
        item.YoutubeVideoId = candidate?.VideoId;

        var minScore = Plugin.Instance?.Configuration.MinMatchScore ?? 80.0;
        if (candidate == null || score < minScore)
        {
            item.Status = "Failed";
            item.Error = $"No suitable YouTube Music match (score {score:0.0}).";
            await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
            return;
        }

        item.Status = "Downloading";
        await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);

        var format = (Plugin.Instance?.Configuration.PreferredFormat ?? "m4a").Trim('.').ToLowerInvariant();
        if (format is not ("m4a" or "mp3" or "opus" or "ogg"))
        {
            format = "m4a";
        }

        var relative = _storage.BuildRelativePath(track, format);
        var absolute = _storage.GetAbsolutePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        try
        {
            var ffmpeg = _ffmpeg.EncoderPath;
            var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(candidate.VideoId, ct).ConfigureAwait(false);
            var audio = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate()
                        ?? throw new InvalidOperationException("No audio streams found.");

            if (string.Equals(format, "m4a", StringComparison.OrdinalIgnoreCase) &&
                audio.Container == Container.Mp4)
            {
                await _youtube.Videos.Streams.DownloadAsync(audio, absolute, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                await _youtube.Videos.DownloadAsync(
                    candidate.VideoId,
                    absolute,
                    o => o
                        .SetFFmpegPath(ffmpeg)
                        .SetContainer(format),
                    cancellationToken: ct).ConfigureAwait(false);
            }

            await EmbedTagsAsync(absolute, track, ct).ConfigureAwait(false);
            await _storage.SaveCoverAsync(Path.GetDirectoryName(absolute)!, track.CoverUrl, ct).ConfigureAwait(false);

            await _store.UpsertTrackIndexAsync(new TrackIndexEntry
            {
                SpotifyTrackId = track.Id,
                YoutubeVideoId = candidate.VideoId,
                MatchScore = score,
                RelativePath = relative,
                Title = track.Name,
                Artists = string.Join(", ", track.Artists),
                Album = track.Album,
                Isrc = track.Isrc
            }, ct).ConfigureAwait(false);

            item.Status = "Completed";
            item.RelativePath = relative;
            item.Error = null;
            await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for {Track}", track.Name);
            item.Status = "Failed";
            item.Error = ex.Message;
            await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EmbedTagsAsync(string path, SpotifyTrackInfo track, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Title = track.Name;
            file.Tag.Performers = track.Artists.ToArray();
            file.Tag.AlbumArtists = [track.AlbumArtist ?? track.Artists.FirstOrDefault() ?? string.Empty];
            file.Tag.Album = track.Album;
            if (track.TrackNumber > 0)
            {
                file.Tag.Track = (uint)track.TrackNumber;
            }

            if (track.DiscNumber > 0)
            {
                file.Tag.Disc = (uint)track.DiscNumber;
            }

            if (track.Year is > 0)
            {
                file.Tag.Year = (uint)track.Year.Value;
            }

            if (!string.IsNullOrEmpty(track.Isrc))
            {
                file.Tag.Comment = $"ISRC:{track.Isrc}; Spotify:{track.Id}";
            }

            try
            {
                if (!string.IsNullOrEmpty(track.CoverUrl))
                {
                    var client = _httpClientFactory.CreateClient();
                    var bytes = client.GetByteArrayAsync(track.CoverUrl, ct).GetAwaiter().GetResult();
                    file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(bytes))];
                }
            }
            catch
            {
                // cover embedding is best-effort
            }

            file.Save();
        }, ct).ConfigureAwait(false);
    }

    private static SpotifyTrackInfo ToTrackInfo(QueueItem item)
    {
        return new SpotifyTrackInfo
        {
            Id = item.SpotifyTrackId,
            Name = item.Title,
            Artists = item.Artists.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
            Album = item.Album,
            AlbumArtist = item.AlbumArtist,
            Isrc = item.Isrc,
            DurationMs = item.DurationMs,
            TrackNumber = item.TrackNumber ?? 0,
            DiscNumber = item.DiscNumber ?? 1,
            Year = item.Year,
            CoverUrl = item.CoverUrl
        };
    }
}
