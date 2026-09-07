using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellySpot.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.YouTubeMusic;

public partial class YouTubeMusicClient
{
    private const string InnertubeKey = "AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30";
    private const string ClientName = "WEB_REMIX";
    private const string ClientVersion = "1.20241030.01.00";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeMusicClient> _logger;

    public YouTubeMusicClient(IHttpClientFactory httpClientFactory, ILogger<YouTubeMusicClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(
        string query,
        string filter,
        CancellationToken ct = default)
    {
        // filter: songs | videos | null
        var body = new Dictionary<string, object?>
        {
            ["context"] = new
            {
                client = new
                {
                    clientName = ClientName,
                    clientVersion = ClientVersion,
                    hl = "en",
                    gl = "US"
                }
            },
            ["query"] = query
        };

        if (!string.IsNullOrEmpty(filter))
        {
            body["params"] = filter switch
            {
                "songs" => "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D",
                "videos" => "EgWKAQIQAWoKEAkQChAFEAMQBA%3D%3D",
                _ => null
            };
        }

        var json = JsonSerializer.Serialize(body);
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://music.youtube.com/youtubei/v1/search?key={InnertubeKey}&prettyPrint=false");
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("YouTube Music search failed: {Status}", response.StatusCode);
            return [];
        }

        return ParseSearchResults(responseBody, query, filter == "songs");
    }

    private static List<MatchCandidate> ParseSearchResults(string json, string query, bool preferSongs)
    {
        var results = new List<MatchCandidate>();
        using var doc = JsonDocument.Parse(json);
        var videoIds = new HashSet<string>(StringComparer.Ordinal);

        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("musicResponsiveListItemRenderer", out var item))
                {
                    var candidate = ParseListItem(item, query, preferSongs);
                    if (candidate != null && videoIds.Add(candidate.VideoId))
                    {
                        results.Add(candidate);
                    }
                }

                foreach (var prop in el.EnumerateObject())
                {
                    Walk(prop.Value);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in el.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }

        Walk(doc.RootElement);
        return results;
    }

    private static MatchCandidate? ParseListItem(JsonElement item, string query, bool songsFilter)
    {
        string? videoId = null;
        if (item.TryGetProperty("playlistItemData", out var pid) && pid.TryGetProperty("videoId", out var vid))
        {
            videoId = vid.GetString();
        }

        if (videoId == null &&
            item.TryGetProperty("overlay", out var overlay) &&
            TryFindVideoId(overlay, out var found))
        {
            videoId = found;
        }

        if (string.IsNullOrEmpty(videoId))
        {
            return null;
        }

        var flex = item.TryGetProperty("flexColumns", out var cols) ? cols : default;
        var texts = new List<string>();
        if (flex.ValueKind == JsonValueKind.Array)
        {
            foreach (var col in flex.EnumerateArray())
            {
                if (!col.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var fr))
                {
                    continue;
                }

                if (!fr.TryGetProperty("text", out var text))
                {
                    continue;
                }

                CollectRuns(text, texts);
            }
        }

        if (texts.Count == 0)
        {
            return null;
        }

        var title = texts[0];
        var artists = new List<string>();
        string? album = null;
        double duration = 0;
        var verified = songsFilter;

        // Typical YTM song row: Title | Artist • Album • Duration  OR Artist • Views • Duration for videos
        if (texts.Count > 1)
        {
            var meta = texts[1];
            var parts = meta.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                artists.AddRange(parts[0].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            }

            foreach (var part in parts.Skip(1))
            {
                if (DurationRegex().IsMatch(part))
                {
                    duration = ParseDuration(part);
                }
                else if (part.Contains("view", StringComparison.OrdinalIgnoreCase))
                {
                    // video views line
                }
                else if (album == null)
                {
                    album = part;
                }
            }
        }

        return new MatchCandidate
        {
            VideoId = videoId,
            Url = songsFilter
                ? $"https://music.youtube.com/watch?v={videoId}"
                : $"https://www.youtube.com/watch?v={videoId}",
            Title = title,
            Author = artists.FirstOrDefault() ?? string.Empty,
            Artists = artists,
            Album = album,
            DurationSeconds = duration,
            Verified = verified,
            SearchQuery = query
        };
    }

    private static bool TryFindVideoId(JsonElement el, out string? videoId)
    {
        videoId = null;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("videoId", out var v) && v.ValueKind == JsonValueKind.String)
            {
                videoId = v.GetString();
                return !string.IsNullOrEmpty(videoId);
            }

            foreach (var prop in el.EnumerateObject())
            {
                if (TryFindVideoId(prop.Value, out videoId))
                {
                    return true;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                if (TryFindVideoId(child, out videoId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void CollectRuns(JsonElement text, List<string> texts)
    {
        if (text.TryGetProperty("runs", out var runs))
        {
            var combined = string.Concat(runs.EnumerateArray()
                .Select(r => r.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty));
            if (!string.IsNullOrWhiteSpace(combined))
            {
                texts.Add(combined.Trim());
            }
        }
    }

    private static double ParseDuration(string value)
    {
        var parts = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var m) &&
            int.TryParse(parts[1], out var s))
        {
            return (m * 60) + s;
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], out var h) &&
            int.TryParse(parts[1], out var m2) &&
            int.TryParse(parts[2], out var s2))
        {
            return (h * 3600) + (m2 * 60) + s2;
        }

        return 0;
    }

    [GeneratedRegex(@"^\d+:\d{2}(:\d{2})?$")]
    private static partial Regex DurationRegex();
}
