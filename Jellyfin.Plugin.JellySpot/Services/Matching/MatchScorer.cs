using System.Text.RegularExpressions;
using FuzzySharp;
using Jellyfin.Plugin.JellySpot.Models;

namespace Jellyfin.Plugin.JellySpot.Services.Matching;

public partial class MatchScorer
{
    private static readonly string[] ForbiddenWords =
    [
        "bassboosted", "remix", "remastered", "reverb", "live", "acoustic", "concert",
        "acapella", "slowed", "instrumental", "cover", "karaoke", "nightcore", "8d"
    ];

    public Dictionary<MatchCandidate, double> OrderResults(IEnumerable<MatchCandidate> results, SpotifyTrackInfo song)
    {
        var scored = new Dictionary<MatchCandidate, double>();
        foreach (var result in results)
        {
            if (!HasCommonWord(song, result))
            {
                continue;
            }

            var artistsMatch = CalcMainArtistMatch(song, result) + CalcArtistsMatch(song, result);
            artistsMatch = Math.Clamp(artistsMatch, 0, 100);
            artistsMatch = ApplyArtistFixups(song, result, artistsMatch);

            var nameMatch = CalcNameMatch(song, result);
            var timeMatch = CalcTimeMatch(song, result);
            var albumMatch = CalcAlbumMatch(song, result);

            var averageMatch = (artistsMatch + nameMatch) / 2.0;

            if (result.Verified && !result.IsrcSearch && !string.IsNullOrEmpty(result.Album) && albumMatch <= 80)
            {
                averageMatch = (averageMatch + albumMatch) / 2.0;
            }

            // Incorporate duration similarly to spotDL weighting.
            averageMatch = (averageMatch + timeMatch) / 2.0;

            if (result.Verified)
            {
                averageMatch *= 1.10;
            }

            if (HasForbiddenMismatch(song, result))
            {
                averageMatch *= 0.5;
            }

            scored[result] = Math.Clamp(averageMatch, 0, 100);
        }

        return scored;
    }

    private static bool HasCommonWord(SpotifyTrackInfo song, MatchCandidate result)
    {
        var songWords = Tokenize($"{song.Name} {string.Join(' ', song.Artists)}");
        var resultWords = Tokenize($"{result.Title} {string.Join(' ', result.Artists)} {result.Author}");
        return songWords.Overlaps(resultWords);
    }

    private static double CalcMainArtistMatch(SpotifyTrackInfo song, MatchCandidate result)
    {
        if (song.Artists.Count == 0)
        {
            return 0;
        }

        var main = song.Artists[0];
        var candidates = new List<string> { result.Author };
        candidates.AddRange(result.Artists);
        return candidates.Max(c => Fuzz.Ratio(Normalize(main), Normalize(c)));
    }

    private static double CalcArtistsMatch(SpotifyTrackInfo song, MatchCandidate result)
    {
        if (song.Artists.Count <= 1)
        {
            return 0;
        }

        var resultText = Normalize(string.Join(' ', result.Artists.Append(result.Author).Append(result.Title)));
        var scores = song.Artists.Skip(1).Select(a => resultText.Contains(Normalize(a), StringComparison.Ordinal) ? 100.0 : Fuzz.PartialRatio(Normalize(a), resultText));
        return scores.DefaultIfEmpty(0).Average() * 0.15;
    }

    private static double ApplyArtistFixups(SpotifyTrackInfo song, MatchCandidate result, double artistsMatch)
    {
        var resultName = Normalize(result.Title);
        if (song.Artists.All(a => resultName.Contains(Normalize(a), StringComparison.Ordinal)))
        {
            artistsMatch = Math.Max(artistsMatch, 90);
        }

        if (song.Artists.Any(a => Normalize(a) == Normalize(result.Author)))
        {
            artistsMatch = Math.Max(artistsMatch, 95);
        }

        return artistsMatch;
    }

    private static double CalcNameMatch(SpotifyTrackInfo song, MatchCandidate result)
    {
        var songName = Normalize(StripFeat(song.Name));
        var resultName = Normalize(StripFeat(result.Title));
        return Fuzz.Ratio(songName, resultName);
    }

    private static double CalcTimeMatch(SpotifyTrackInfo song, MatchCandidate result)
    {
        if (result.DurationSeconds <= 0 || song.DurationMs <= 0)
        {
            return 50;
        }

        var songSeconds = song.DurationMs / 1000.0;
        var diff = Math.Abs(songSeconds - result.DurationSeconds);
        // Exponential-ish decay: 0s => 100, 5s => ~67, 15s => ~20
        return Math.Clamp(100 * Math.Exp(-diff / 8.0), 0, 100);
    }

    private static double CalcAlbumMatch(SpotifyTrackInfo song, MatchCandidate result)
    {
        if (string.IsNullOrWhiteSpace(song.Album) || string.IsNullOrWhiteSpace(result.Album))
        {
            return 50;
        }

        return Fuzz.Ratio(Normalize(song.Album), Normalize(result.Album));
    }

    private static bool HasForbiddenMismatch(SpotifyTrackInfo song, MatchCandidate result)
    {
        var songText = Normalize($"{song.Name} {song.Album}");
        var resultText = Normalize($"{result.Title} {result.Album}");
        foreach (var word in ForbiddenWords)
        {
            if (resultText.Contains(word, StringComparison.Ordinal) && !songText.Contains(word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripFeat(string value)
    {
        return FeatRegex().Replace(value, string.Empty).Trim();
    }

    private static string Normalize(string value)
    {
        value = value.ToLowerInvariant();
        value = NonWordRegex().Replace(value, " ");
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static HashSet<string> Tokenize(string value)
    {
        return Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\((feat|ft|with)\.?.*?\)|\[(feat|ft|with)\.?.*?\]", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex NonWordRegex();
}
