using Jellyfin.Plugin.JellySpot.Models;

namespace Jellyfin.Plugin.JellySpot.Helpers;

public static class TransformationPatches
{
    public static string IndexHtml(PatchRequestPayload payload)
    {
        string version = Plugin.Instance?.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        string cacheParam = $"?v={version}";
        string cssLinks = $"<link rel=\"stylesheet\" href=\"../JellySpot/jellyspot-tabs.css{cacheParam}\" />";
        string scripts = $"<script defer src=\"../JellySpot/jellyspot-tabs.js{cacheParam}\"></script>";

        return payload.Contents!
            .Replace("</head>", $"{cssLinks}</head>", StringComparison.Ordinal)
            .Replace("</body>", $"{scripts}</body>", StringComparison.Ordinal);
    }
}
