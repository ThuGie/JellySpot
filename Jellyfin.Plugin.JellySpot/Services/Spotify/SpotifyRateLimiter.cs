using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Spotify;

public class SpotifyRateLimiter
{
    private readonly ILogger<SpotifyRateLimiter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTime> _window = new();
    private DateTime _pausedUntil = DateTime.MinValue;

    public SpotifyRateLimiter(ILogger<SpotifyRateLimiter> logger)
    {
        _logger = logger;
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var rps = Math.Max(0.2, config?.SpotifyRequestsPerSecond ?? 2.0);
        var maxConcurrent = Math.Max(1, config?.SpotifyMaxConcurrentRequests ?? 2);
        var windowLimit = Math.Max(1, (int)Math.Ceiling(rps * 30));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                if (now < _pausedUntil)
                {
                    var delay = _pausedUntil - now;
                    _gate.Release();
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                while (_window.Count > 0 && (now - _window.Peek()).TotalSeconds > 30)
                {
                    _window.Dequeue();
                }

                if (_window.Count >= windowLimit)
                {
                    var wait = TimeSpan.FromSeconds(30) - (now - _window.Peek()) + TimeSpan.FromMilliseconds(50);
                    _gate.Release();
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                // Soft concurrency: leave headroom by spacing requests.
                if (_window.Count > 0)
                {
                    var minGap = TimeSpan.FromSeconds(1.0 / rps);
                    var sinceLast = now - _window.Last();
                    if (sinceLast < minGap)
                    {
                        var wait = minGap - sinceLast;
                        _gate.Release();
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                _window.Enqueue(now);
                return;
            }
            finally
            {
                if (_gate.CurrentCount == 0)
                {
                    try
                    {
                        _gate.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                        // ignored
                    }
                }
            }
        }
    }

    public void NotifyRateLimited(int retryAfterSeconds)
    {
        var pause = Math.Max(1, retryAfterSeconds);
        var jitter = Random.Shared.Next(250, 1500);
        _pausedUntil = DateTime.UtcNow.AddSeconds(pause).AddMilliseconds(jitter);
        _logger.LogWarning("Spotify rate limited. Pausing for {Seconds}s", pause);
    }
}
