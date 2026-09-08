using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellySpot.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public partial class LibraryStorage
{
    private readonly JellySpotStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LibraryMatchService _libraryMatch;
    private readonly ILogger<LibraryStorage> _logger;

    public LibraryStorage(
        JellySpotStore store,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        LibraryMatchService libraryMatch,
        ILogger<LibraryStorage> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _libraryMatch = libraryMatch;
        _logger = logger;
    }

    public string GetStorageRoot()
    {
        var root = LibraryCatalog.ResolveStorageRoot(_libraryManager, _logger);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "Storage root is not configured. Pick a music library or set a folder under Dashboard → JellySpot.");
        }

        Directory.CreateDirectory(root);
        return root;
    }

    public string BuildRelativePath(SpotifyTrackInfo track, string extension)
    {
        var albumArtist = Sanitize(track.AlbumArtist ?? track.Artists.FirstOrDefault() ?? "Unknown Artist");
        var album = ResolveAlbumFolderName(albumArtist, track);
        var trackNo = track.TrackNumber > 0 ? $"{track.TrackNumber:00} - " : string.Empty;
        var title = Sanitize(track.Name);
        var fileName = $"{trackNo}{title}.{extension.TrimStart('.')}";
        return Path.Combine(albumArtist, album, fileName);
    }

    private string ResolveAlbumFolderName(string albumArtist, SpotifyTrackInfo track)
    {
        var fallback = Sanitize(track.Album);
        if (track.Year is > 0)
        {
            fallback = $"{fallback} ({track.Year})";
        }

        try
        {
            var artistDir = Path.Combine(GetStorageRoot(), albumArtist);
            if (!Directory.Exists(artistDir))
            {
                return fallback;
            }

            var artists = track.Artists.ToList();
            if (!string.IsNullOrWhiteSpace(track.AlbumArtist)
                && !artists.Contains(track.AlbumArtist, StringComparer.OrdinalIgnoreCase))
            {
                artists.Insert(0, track.AlbumArtist);
            }

            string? bestName = null;
            var bestCount = -1;
            var bestScore = 0;
            var bestLength = int.MaxValue;
            foreach (var dir in Directory.EnumerateDirectories(artistDir))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var score = LibraryMatchService.AlbumFolderScore(name, track.Album, artists);
                if (score < 90)
                {
                    continue;
                }

                var count = CountAudioFiles(dir);
                if (count > bestCount
                    || (count == bestCount && score > bestScore)
                    || (count == bestCount && score == bestScore && name.Length < bestLength))
                {
                    bestName = name;
                    bestCount = count;
                    bestScore = score;
                    bestLength = name.Length;
                }
            }

            return bestName ?? fallback;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not reuse an existing album folder for {Album}", track.Album);
            return fallback;
        }
    }

    private static int CountAudioFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Count(path => AudioExtensionRegex().IsMatch(Path.GetExtension(path)));
        }
        catch
        {
            return 0;
        }
    }

    public string GetAbsolutePath(string relativePath)
    {
        return Path.Combine(GetStorageRoot(), relativePath);
    }

    public async Task<bool> TrackFileExistsAsync(string spotifyTrackId, CancellationToken ct = default)
    {
        return await FindExistingRelativePathAsync(spotifyTrackId, null, ct).ConfigureAwait(false) != null;
    }

    public bool IsInJellyfinLibrary(SpotifyTrackInfo track)
    {
        return _libraryMatch.Contains(track);
    }

    public async Task<bool> IsOwnedAsync(SpotifyTrackInfo track, CancellationToken ct = default)
    {
        if (await FindExistingRelativePathAsync(track, ct).ConfigureAwait(false) != null)
        {
            return true;
        }

        return _libraryMatch.Contains(track);
    }

    public async Task<string?> FindExistingRelativePathAsync(SpotifyTrackInfo track, CancellationToken ct = default)
    {
        return await FindExistingRelativePathAsync(track.Id, track, ct).ConfigureAwait(false);
    }

    public async Task<string?> FindExistingRelativePathAsync(
        string spotifyTrackId,
        SpotifyTrackInfo? track,
        CancellationToken ct = default)
    {
        var entry = await _store.GetTrackIndexAsync(spotifyTrackId, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(entry?.RelativePath) && FileExistsNonEmpty(GetAbsolutePath(entry.RelativePath)))
        {
            return entry.RelativePath;
        }

        if (track == null)
        {
            return null;
        }

        var preferred = (Plugin.Instance?.Configuration.PreferredFormat ?? "m4a").Trim('.').ToLowerInvariant();
        foreach (var ext in new[] { preferred, "m4a", "mp3", "flac", "opus", "ogg" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relative = BuildRelativePath(track, ext);
            if (FileExistsNonEmpty(GetAbsolutePath(relative)))
            {
                return relative;
            }
        }

        return null;
    }

    private static bool FileExistsNonEmpty(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task SaveCoverAsync(string albumFolderAbsolute, string? coverUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return;
        }

        var coverPath = Path.Combine(albumFolderAbsolute, "cover.jpg");
        if (File.Exists(coverPath))
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var bytes = await client.GetByteArrayAsync(coverUrl, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(coverPath, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to download cover art");
        }
    }

    public async Task WritePlaylistM3u8Async(
        string playlistName,
        IEnumerable<string> relativeTrackPaths,
        CancellationToken ct = default)
    {
        var root = GetStorageRoot();
        var playlistsDir = Path.Combine(root, "Playlists");
        Directory.CreateDirectory(playlistsDir);
        var filePath = Path.Combine(playlistsDir, Sanitize(playlistName) + ".m3u8");

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var relative in relativeTrackPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var absolute = GetAbsolutePath(relative);
            if (!File.Exists(absolute))
            {
                continue;
            }

            sb.AppendLine($"#EXTINF:-1,{Path.GetFileNameWithoutExtension(relative)}");
            // Relative from playlist file to track
            var rel = Path.GetRelativePath(playlistsDir, absolute).Replace('\\', '/');
            sb.AppendLine(rel);
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), ct).ConfigureAwait(false);
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim().Trim('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^\.(m4a|mp3|flac|opus|ogg|aac|wma|wav)$", RegexOptions.IgnoreCase)]
    private static partial Regex AudioExtensionRegex();
}
