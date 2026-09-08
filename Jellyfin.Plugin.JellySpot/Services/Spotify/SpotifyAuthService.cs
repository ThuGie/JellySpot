using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Spotify;

public class SpotifyAuthService
{
    private static readonly string[] Scopes =
    [
        "user-library-read",
        "user-read-private",
        "playlist-read-private",
        "playlist-read-collaborative",
        "user-follow-read"
    ];

    private readonly JellySpotStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpotifyAuthService> _logger;

    public SpotifyAuthService(
        JellySpotStore store,
        IHttpClientFactory httpClientFactory,
        ILogger<SpotifyAuthService> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> CreateAuthorizationUrlAsync(Guid jellyfinUserId, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration
                     ?? throw new InvalidOperationException("Plugin not initialized.");
        if (string.IsNullOrWhiteSpace(config.SpotifyClientId))
        {
            throw new InvalidOperationException("Spotify Client ID is not configured.");
        }

        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await _store.SaveOAuthStateAsync(state, jellyfinUserId, verifier, ct).ConfigureAwait(false);

        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.SpotifyClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = config.SpotifyRedirectUri,
            ["state"] = state,
            ["scope"] = string.Join(' ', Scopes),
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["show_dialog"] = "true"
        };

        return "https://accounts.spotify.com/authorize?" +
               string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task HandleCallbackAsync(string code, string state, CancellationToken ct = default)
    {
        var oauth = await _store.TakeOAuthStateAsync(state, ct).ConfigureAwait(false);
        if (oauth is null)
        {
            throw new InvalidOperationException("Invalid or expired OAuth state.");
        }

        var tokens = await ExchangeCodeAsync(code, oauth.Value.CodeVerifier, ct).ConfigureAwait(false);
        var profile = await FetchProfileAsync(tokens.AccessToken, ct).ConfigureAwait(false);
        tokens.SpotifyUserId = profile.UserId;
        tokens.DisplayName = profile.DisplayName;
        await _store.SaveTokensAsync(oauth.Value.UserId, tokens, ct).ConfigureAwait(false);
        _logger.LogInformation("Linked Spotify account {DisplayName} to Jellyfin user {UserId}", tokens.DisplayName, oauth.Value.UserId);
    }

    public async Task<SpotifyTokens?> GetValidTokensAsync(Guid jellyfinUserId, CancellationToken ct = default)
    {
        var tokens = await _store.GetTokensAsync(jellyfinUserId, ct).ConfigureAwait(false);
        if (tokens == null)
        {
            return null;
        }

        if (tokens.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return tokens;
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            return null;
        }

        var refreshed = await RefreshAsync(tokens.RefreshToken, ct).ConfigureAwait(false);
        refreshed.SpotifyUserId = tokens.SpotifyUserId;
        refreshed.DisplayName = tokens.DisplayName;
        if (string.IsNullOrEmpty(refreshed.RefreshToken))
        {
            refreshed.RefreshToken = tokens.RefreshToken;
        }

        await _store.SaveTokensAsync(jellyfinUserId, refreshed, ct).ConfigureAwait(false);
        return refreshed;
    }

    public Task UnlinkAsync(Guid jellyfinUserId, CancellationToken ct = default)
    {
        return _store.DeleteTokensAsync(jellyfinUserId, ct);
    }

    private async Task<SpotifyTokens> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var client = _httpClientFactory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = config.SpotifyRedirectUri,
            ["client_id"] = config.SpotifyClientId,
            ["code_verifier"] = verifier
        });

        if (!string.IsNullOrWhiteSpace(config.SpotifyClientSecret))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.SpotifyClientId}:{config.SpotifyClientSecret}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var response = await client.PostAsync("https://accounts.spotify.com/api/token", content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spotify token exchange failed: {body}");
        }

        return ParseTokenResponse(body);
    }

    private async Task<SpotifyTokens> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var client = _httpClientFactory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = config.SpotifyClientId
        });

        if (!string.IsNullOrWhiteSpace(config.SpotifyClientSecret))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.SpotifyClientId}:{config.SpotifyClientSecret}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var response = await client.PostAsync("https://accounts.spotify.com/api/token", content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spotify token refresh failed: {body}");
        }

        return ParseTokenResponse(body);
    }

    private async Task<(string UserId, string DisplayName)> FetchProfileAsync(string accessToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
        var name = doc.RootElement.TryGetProperty("display_name", out var dn)
            ? dn.GetString() ?? id
            : id;
        return (id, name);
    }

    private static SpotifyTokens ParseTokenResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        return new SpotifyTokens
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? string.Empty,
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty,
            Scope = root.TryGetProperty("scope", out var scope) ? scope.GetString() ?? string.Empty : string.Empty,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn)
        };
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64Url(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
