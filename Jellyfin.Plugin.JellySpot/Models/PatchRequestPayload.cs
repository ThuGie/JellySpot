using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellySpot.Models;

public class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
