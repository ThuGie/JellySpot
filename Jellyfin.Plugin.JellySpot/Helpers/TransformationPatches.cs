using Jellyfin.Plugin.JellySpot.Models;

namespace Jellyfin.Plugin.JellySpot.Helpers;

public static class TransformationPatches
{
    public static string IndexHtml(PatchRequestPayload payload)
    {
        string version = Plugin.Instance?.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        string cacheParam = $"?v={version}";
        string script =
            $"<script defer src=\"../JellySpot/jellyspot-nav.js{cacheParam}\"></script>";

        return payload.Contents!
            .Replace("</body>", $"{script}</body>", StringComparison.Ordinal);
    }
}
