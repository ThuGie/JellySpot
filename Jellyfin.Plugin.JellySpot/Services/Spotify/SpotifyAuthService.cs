using System.Net.Http.Headers;
using System.Collections.Concurrent;
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
        "user-follow-read",
        "user-top-read",
        "user-read-recently-played"
    ];

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private SemaphoreSlim Gate(Guid id) => _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
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
        var userId = await _store.GetOAuthStateUserAsync(state, ct)
            ?? throw new InvalidOperationException("Invalid or expired OAuth state.");
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            var oauth = await _store.TakeOAuthStateAsync(state, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Invalid or expired OAuth state.");
            var tokens = await ExchangeCodeAsync(code, oauth.CodeVerifier, ct).ConfigureAwait(false);
            var profile = await FetchProfileAsync(tokens.AccessToken, ct).ConfigureAwait(false);
            tokens.SpotifyUserId = profile.UserId;
            tokens.DisplayName = profile.DisplayName;
            tokens.Generation = Guid.NewGuid().ToString("N");
            await _store.ClearUserCacheAsync(oauth.UserId, ct);
            await _store.SaveTokensAsync(oauth.UserId, tokens, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<SpotifyTokens?> GetValidTokensAsync(Guid jellyfinUserId, CancellationToken ct = default,
        bool forceRefresh = false, string? rejectedToken = null)
    {
        var gate = Gate(jellyfinUserId);
        await gate.WaitAsync(ct);
        try
        {
            var tokens = await _store.GetTokensAsync(jellyfinUserId, ct).ConfigureAwait(false);
            if (tokens == null || tokens.RelinkRequired) return null;
            if (rejectedToken != null && tokens.AccessToken != rejectedToken) return tokens;
            if (!forceRefresh && tokens.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2)) return tokens;
            if (string.IsNullOrEmpty(tokens.RefreshToken))
            {
                tokens.RelinkRequired = true;
                await _store.SaveTokensAsync(jellyfinUserId, tokens, ct);
                return null;
            }
            SpotifyTokens refreshed;
            try { refreshed = await RefreshAsync(tokens.RefreshToken, ct).ConfigureAwait(false); }
            catch (SpotifyAuthorizationException)
            {
                tokens.RelinkRequired = true;
                await _store.SaveTokensAsync(jellyfinUserId, tokens, ct);
                return null;
            }
            refreshed.Generation = tokens.Generation;
            refreshed.SpotifyUserId = tokens.SpotifyUserId;
            refreshed.DisplayName = tokens.DisplayName;
            if (string.IsNullOrEmpty(refreshed.Scope)) refreshed.Scope = tokens.Scope;
            if (string.IsNullOrEmpty(refreshed.RefreshToken)) refreshed.RefreshToken = tokens.RefreshToken;
            await _store.SaveTokensAsync(jellyfinUserId, refreshed, ct).ConfigureAwait(false);
            return refreshed;
        }
        finally { gate.Release(); }
    }

    public async Task EnsureGenerationAsync(Guid userId, string generation, CancellationToken ct)
    {
        var tokens = await _store.GetTokensAsync(userId, ct);
        if (tokens == null || tokens.RelinkRequired || tokens.Generation != generation)
            throw new InvalidOperationException("Spotify account changed. Please retry.");
    }

    public async Task MarkRejectedAsync(Guid userId, string generation, CancellationToken ct)
    {
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            var tokens = await _store.GetTokensAsync(userId, ct);
            if (tokens?.Generation != generation) return;
            tokens.RelinkRequired = true;
            await _store.SaveTokensAsync(userId, tokens, ct);
        }
        finally { gate.Release(); }
    }

    public async Task UnlinkAsync(Guid jellyfinUserId, CancellationToken ct = default)
    {
        var gate = Gate(jellyfinUserId);
        await gate.WaitAsync(ct);
        try
        {
            await _store.DeleteTokensAsync(jellyfinUserId, ct);
            await _store.ClearUserCacheAsync(jellyfinUserId, ct);
        }
        finally { gate.Release(); }
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
            throw new InvalidOperationException("Spotify authorization failed. Please try linking again.");
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
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && body.Contains("invalid_grant", StringComparison.Ordinal))
                throw new SpotifyAuthorizationException();
            throw new HttpRequestException("Spotify token refresh is temporarily unavailable.");
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

public sealed class SpotifyAuthorizationException : Exception { }
