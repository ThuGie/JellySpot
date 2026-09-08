using System.Diagnostics;
using System.Net;
using System.Net.Http;
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

        var existingPath = await _storage.FindExistingRelativePathAsync(track, ct).ConfigureAwait(false);
        if (existingPath != null)
        {
            var existing = await _store.GetTrackIndexAsync(track.Id, ct).ConfigureAwait(false);
            if (existing == null || !string.Equals(existing.RelativePath, existingPath, StringComparison.OrdinalIgnoreCase))
            {
                await _store.UpsertTrackIndexAsync(new TrackIndexEntry
                {
                    SpotifyTrackId = track.Id,
                    YoutubeVideoId = existing?.YoutubeVideoId ?? item.YoutubeVideoId,
                    MatchScore = existing?.MatchScore ?? item.MatchScore,
                    RelativePath = existingPath,
                    Title = track.Name,
                    Artists = string.Join(", ", track.Artists),
                    Album = track.Album,
                    Isrc = track.Isrc
                }, ct).ConfigureAwait(false);
            }

            item.Status = "Completed";
            item.RelativePath = existingPath;
            item.YoutubeVideoId = existing?.YoutubeVideoId ?? item.YoutubeVideoId;
            item.MatchScore = existing?.MatchScore ?? item.MatchScore;
            item.Error = null;
            await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
            _logger.LogInformation("Skipping {Track}; file already exists at {Path}", track.Name, existingPath);
            return;
        }

        if (_storage.IsInJellyfinLibrary(track))
        {
            item.Status = "Completed";
            item.RelativePath = null;
            item.Error = null;
            await _store.UpdateQueueItemAsync(item, ct).ConfigureAwait(false);
            _logger.LogInformation("Skipping {Track}; already in the Jellyfin library", track.Name);
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
            await DownloadAudioAsync(candidate.VideoId, absolute, format, ct).ConfigureAwait(false);

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

    private async Task DownloadAudioAsync(string videoId, string absolute, string format, CancellationToken ct)
    {
        var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct).ConfigureAwait(false);
        var audios = PickAudioStreams(streamManifest.GetAudioOnlyStreams().ToList(), format);
        if (audios.Count == 0)
        {
            throw new InvalidOperationException("No audio streams found.");
        }

        Exception? lastForbidden = null;
        foreach (var audio in audios)
        {
            var temp = absolute + ".src" + audio.Container.Name;
            try
            {
                await _youtube.Videos.Streams.DownloadAsync(audio, temp, cancellationToken: ct).ConfigureAwait(false);
                if (ContainerMatchesFormat(audio.Container, format))
                {
                    File.Move(temp, absolute, overwrite: true);
                }
                else
                {
                    await ConvertAudioAsync(temp, absolute, format, TargetBitrateKbps(audio), ct).ConfigureAwait(false);
                    TryDelete(temp);
                }

                return;
            }
            catch (HttpRequestException ex) when (IsForbidden(ex))
            {
                lastForbidden = ex;
                TryDelete(temp);
                _logger.LogWarning("YouTube returned 403 for {VideoId} ({Container} {Bitrate}); trying next stream",
                    videoId, audio.Container, audio.Bitrate);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        throw lastForbidden ?? new InvalidOperationException("No playable audio streams.");
    }

    private static List<IAudioStreamInfo> PickAudioStreams(IReadOnlyList<IAudioStreamInfo> streams, string format)
    {
        var quality = (Plugin.Instance?.Configuration.AudioQuality ?? "highest").Trim().ToLowerInvariant();
        var ordered = streams
            .OrderByDescending(s => s.Bitrate.BitsPerSecond)
            .ThenByDescending(s => ContainerMatchesFormat(s.Container, format))
            .ToList();

        if (ordered.Count == 0)
        {
            return ordered;
        }

        var selected = quality switch
        {
            "low" => ClosestBitrate(ordered, 64_000),
            "medium" => ClosestBitrate(ordered, 128_000),
            "high" => ordered.FirstOrDefault(s => s.Bitrate.BitsPerSecond >= 128_000) ?? ordered[0],
            _ => ordered[0]
        };

        return new[] { selected }.Concat(ordered.Where(s => !ReferenceEquals(s, selected))).ToList();
    }

    private static IAudioStreamInfo ClosestBitrate(IReadOnlyList<IAudioStreamInfo> streams, long target)
    {
        return streams.OrderBy(s => Math.Abs(s.Bitrate.BitsPerSecond - target)).First();
    }

    private static bool ContainerMatchesFormat(Container container, string format)
    {
        var name = container.Name.ToLowerInvariant();
        return format switch
        {
            "m4a" => name is "mp4" or "m4a" or "m4b",
            "mp3" => name == "mp3",
            "opus" => name is "webm" or "opus",
            "ogg" => name is "ogg" or "oggs" or "opus" or "webm",
            _ => false
        };
    }

    private static int TargetBitrateKbps(IAudioStreamInfo audio)
    {
        var source = Math.Max(64, (int)Math.Round(audio.Bitrate.BitsPerSecond / 1000.0));
        return (Plugin.Instance?.Configuration.AudioQuality ?? "highest").Trim().ToLowerInvariant() switch
        {
            "low" => Math.Min(source, 96),
            "medium" => Math.Min(source, 128),
            "high" => Math.Min(source, 192),
            _ => source
        };
    }

    private async Task ConvertAudioAsync(string source, string dest, string format, int bitrateKbps, CancellationToken ct)
    {
        var codec = format switch
        {
            "mp3" => "libmp3lame",
            "opus" => "libopus",
            "ogg" => "libvorbis",
            _ => "aac"
        };

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg.EncoderPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-y", "-i", source, "-vn", "-c:a", codec, "-b:a", bitrateKbps + "k", dest })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(dest))
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException("FFmpeg convert failed: " + stderr.Trim());
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // temp cleanup is best-effort
        }
    }

    private static bool IsForbidden(HttpRequestException ex)
    {
        return ex.StatusCode == HttpStatusCode.Forbidden ||
               ex.Message.Contains("403", StringComparison.Ordinal);
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
