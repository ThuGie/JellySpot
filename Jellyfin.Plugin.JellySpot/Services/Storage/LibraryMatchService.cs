using System.Text.RegularExpressions;
using FuzzySharp;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellySpot.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public partial class LibraryMatchService
{
    private static readonly string[] VersionWords =
    [
        "remix", "live", "acoustic", "concert", "acapella", "slowed",
        "instrumental", "cover", "karaoke", "nightcore", "8d", "bassboosted"
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryMatchService> _logger;
    private readonly object _gate = new();
    private Catalog _catalog = Catalog.Empty;
    private DateTime _builtAtUtc = DateTime.MinValue;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public LibraryMatchService(ILibraryManager libraryManager, ILogger<LibraryMatchService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public void Warmup()
    {
        _ = GetCatalog();
    }

    public bool Contains(SpotifyTrackInfo track)
    {
        if (track == null)
        {
            return false;
        }

        var catalog = GetCatalog();
        if (catalog.Count == 0)
        {
            return false;
        }

        var isrc = NormalizeIsrc(track.Isrc);
        if (!string.IsNullOrEmpty(isrc) && catalog.ByIsrc.ContainsKey(isrc))
        {
            return true;
        }

        var title = NormalizeTitle(track.Name);
        if (string.IsNullOrEmpty(title) || track.Artists.Count == 0)
        {
            return false;
        }

        var candidates = LookupByTitle(catalog, title);
        if (candidates.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (IsMatch(track, title, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private Catalog GetCatalog()
    {
        lock (_gate)
        {
            var ttl = _catalog.Count == 0 ? TimeSpan.FromSeconds(30) : Ttl;
            if (_builtAtUtc != DateTime.MinValue && DateTime.UtcNow - _builtAtUtc < ttl)
            {
                return _catalog;
            }
        }

        Catalog built;
        try
        {
            built = Build();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellySpot could not scan the Jellyfin music library for existing tracks");
            built = Catalog.Empty;
        }

        lock (_gate)
        {
            _catalog = built;
            _builtAtUtc = DateTime.UtcNow;
            return _catalog;
        }
    }

    private Catalog Build()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsVirtualItem = false
        };

        var musicIds = LibraryCatalog.ListLibraries(_libraryManager, _logger)
            .Where(l => LibraryCatalog.IsMusic(l.CollectionType))
            .Select(l => Guid.TryParse(l.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
        if (musicIds.Length > 0)
        {
            query.AncestorIds = musicIds;
        }

        var items = _libraryManager.GetItemList(query);
        var byIsrc = new Dictionary<string, LibraryAudio>(StringComparer.Ordinal);
        var byTitle = new Dictionary<string, List<LibraryAudio>>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (item is not Audio audio)
            {
                continue;
            }

            var name = audio.Name ?? string.Empty;
            var title = NormalizeTitle(name);
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            var artists = (audio.Artists?.Count > 0 ? audio.Artists : audio.AlbumArtists) ?? [];
            var durationMs = audio.RunTimeTicks is > 0
                ? (int)(audio.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond)
                : 0;
            var artistNames = artists.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
            var entry = new LibraryAudio(
                name,
                title,
                artistNames.Select(NormalizeArtist).Where(a => a.Length > 0).ToArray(),
                audio.Album ?? string.Empty,
                NormalizeAlbumKey(audio.Album ?? string.Empty, artistNames),
                ReadIsrc(audio),
                durationMs);

            if (!string.IsNullOrEmpty(entry.Isrc))
            {
                byIsrc.TryAdd(entry.Isrc, entry);
            }

            foreach (var key in TitleKeys(name, title))
            {
                AddTitleKey(byTitle, key, entry);
            }
        }

        _logger.LogInformation(
            "JellySpot indexed {Count} audio files for in-library matching",
            items.Count);

        return new Catalog(items.Count, byIsrc, byTitle);
    }

    private static void AddTitleKey(Dictionary<string, List<LibraryAudio>> byTitle, string key, LibraryAudio entry)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!byTitle.TryGetValue(key, out var list))
        {
            list = [];
            byTitle[key] = list;
        }

        list.Add(entry);
    }

    private static IEnumerable<string> TitleKeys(string rawName, string normalizedTitle)
    {
        yield return normalizedTitle;
        var stripped = NormalizeTitle(rawName);
        if (!string.Equals(stripped, normalizedTitle, StringComparison.Ordinal))
        {
            yield return stripped;
        }

        var prefix = TitlePrefix(normalizedTitle);
        if (!string.IsNullOrEmpty(prefix) && !string.Equals(prefix, normalizedTitle, StringComparison.Ordinal))
        {
            yield return prefix;
        }
    }

    private static List<LibraryAudio> LookupByTitle(Catalog catalog, string title)
    {
        var found = new List<LibraryAudio>();
        var seen = new HashSet<LibraryAudio>();
        foreach (var key in TitleKeys(title, title))
        {
            if (!catalog.ByTitle.TryGetValue(key, out var matches))
            {
                continue;
            }

            foreach (var match in matches)
            {
                if (seen.Add(match))
                {
                    found.Add(match);
                }
            }
        }

        return found;
    }

    private static bool IsMatch(SpotifyTrackInfo track, string normalizedTitle, LibraryAudio candidate)
    {
        if (HasVersionMismatch(track, candidate))
        {
            return false;
        }

        var titleScore = Fuzz.Ratio(normalizedTitle, candidate.NormalizedTitle);
        var artistScore = BestArtistScore(track, candidate);
        if (artistScore < 88 || titleScore < 92)
        {
            return false;
        }

        var hasDuration = track.DurationMs > 0 && candidate.DurationMs > 0;
        if (hasDuration)
        {
            return Math.Abs(track.DurationMs - candidate.DurationMs) <= 3500;
        }

        if (titleScore < 97 || artistScore < 95)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(track.Album) || string.IsNullOrWhiteSpace(candidate.Album))
        {
            return false;
        }

        return Fuzz.Ratio(NormalizeAlbumKey(track.Album, track.Artists), candidate.NormalizedAlbum) >= 85;
    }

    private static int BestArtistScore(SpotifyTrackInfo track, LibraryAudio candidate)
    {
        if (candidate.Artists.Length == 0)
        {
            return 0;
        }

        var best = 0;
        foreach (var artist in track.Artists)
        {
            var normalized = NormalizeArtist(artist);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            foreach (var libraryArtist in candidate.Artists)
            {
                best = Math.Max(best, Fuzz.Ratio(normalized, libraryArtist));
            }
        }

        return best;
    }

    private static bool HasVersionMismatch(SpotifyTrackInfo track, LibraryAudio candidate)
    {
        var songText = Normalize($"{track.Name} {track.Album}");
        var libraryText = Normalize($"{candidate.Name} {candidate.Album}");
        foreach (var word in VersionWords)
        {
            var inSong = songText.Contains(word, StringComparison.Ordinal);
            var inLibrary = libraryText.Contains(word, StringComparison.Ordinal);
            if (inSong != inLibrary)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadIsrc(BaseItem item)
    {
        var direct = item.GetProviderId("ISRC") ?? item.GetProviderId("Isrc");
        var normalized = NormalizeIsrc(direct);
        if (!string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        if (item.ProviderIds == null)
        {
            return null;
        }

        foreach (var pair in item.ProviderIds)
        {
            if (pair.Key.Contains("isrc", StringComparison.OrdinalIgnoreCase))
            {
                normalized = NormalizeIsrc(pair.Value);
                if (!string.IsNullOrEmpty(normalized))
                {
                    return normalized;
                }
            }
        }

        return null;
    }

    private static string NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return IsrcRegex().Replace(value, string.Empty).ToUpperInvariant();
    }

    public static string NormalizeAlbumKey(string album, IEnumerable<string>? artists)
    {
        var normalized = Normalize(StripEdition(album ?? string.Empty));
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        foreach (var artist in (artists ?? []).OrderByDescending(a => a.Length))
        {
            var artistKey = NormalizeArtist(artist);
            if (artistKey.Length < 2)
            {
                continue;
            }

            if (normalized.StartsWith(artistKey + " ", StringComparison.Ordinal))
            {
                var stripped = normalized[(artistKey.Length + 1)..];
                if (!string.IsNullOrEmpty(stripped))
                {
                    normalized = stripped;
                    break;
                }
            }
        }

        var yearCut = TrailingYearRegex().Match(normalized);
        if (yearCut.Success && !string.IsNullOrEmpty(yearCut.Groups[1].Value))
        {
            normalized = yearCut.Groups[1].Value;
        }

        return normalized;
    }

    public static int AlbumFolderScore(string folderName, string album, IEnumerable<string>? artists)
    {
        var left = NormalizeAlbumKey(folderName, artists);
        var right = NormalizeAlbumKey(album, artists);
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return 0;
        }

        return Fuzz.Ratio(left, right);
    }

    private static string NormalizeTitle(string value)
    {
        return Normalize(StripEdition(StripFeat(value)));
    }

    private static string TitlePrefix(string normalizedTitle)
    {
        var words = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3 ? words[0] + " " + words[1] : string.Empty;
    }

    private static string NormalizeArtist(string value)
    {
        var normalized = Normalize(value);
        if (normalized.StartsWith("the ", StringComparison.Ordinal) && normalized.Length > 4)
        {
            normalized = normalized[4..];
        }

        return normalized;
    }

    private static string StripFeat(string value)
    {
        return FeatRegex().Replace(value ?? string.Empty, string.Empty).Trim();
    }

    private static string StripEdition(string value)
    {
        return EditionRegex().Replace(value ?? string.Empty, string.Empty).Trim();
    }

    private static string Normalize(string value)
    {
        value = (value ?? string.Empty).ToLowerInvariant();
        value = NonWordRegex().Replace(value, " ");
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"\((feat|ft|with)\.?.*?\)|\[(feat|ft|with)\.?.*?\]", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();

    [GeneratedRegex(@"\s*[\(\[]\s*(clean|explicit)(\s+version)?\s*[\)\]]|\s*-\s*(clean|explicit)\s*$|\s*[\(\[]\s*(deluxe|expanded|bonus|anniversary)(\s+(edition|version|track|tracks))?\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex EditionRegex();

    [GeneratedRegex(@"^(.*)\s+(?:19|20)\d{2}$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"[\s-]")]
    private static partial Regex IsrcRegex();

    private sealed record LibraryAudio(
        string Name,
        string NormalizedTitle,
        string[] Artists,
        string Album,
        string NormalizedAlbum,
        string? Isrc,
        int DurationMs);

    private sealed class Catalog
    {
        public static readonly Catalog Empty = new(0, new Dictionary<string, LibraryAudio>(StringComparer.Ordinal), new Dictionary<string, List<LibraryAudio>>(StringComparer.Ordinal));

        public Catalog(
            int count,
            Dictionary<string, LibraryAudio> byIsrc,
            Dictionary<string, List<LibraryAudio>> byTitle)
        {
            Count = count;
            ByIsrc = byIsrc;
            ByTitle = byTitle;
        }

        public int Count { get; }

        public Dictionary<string, LibraryAudio> ByIsrc { get; }

        public Dictionary<string, List<LibraryAudio>> ByTitle { get; }
    }
}
