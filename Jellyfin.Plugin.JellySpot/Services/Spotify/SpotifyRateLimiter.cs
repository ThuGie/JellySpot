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
        var windowLimit = Math.Max(1, (int)Math.Ceiling(rps * 30));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan? delay = null;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                if (now < _pausedUntil)
                {
                    delay = _pausedUntil - now;
                }
                else
                {
                    while (_window.Count > 0 && (now - _window.Peek()).TotalSeconds > 30)
                    {
                        _window.Dequeue();
                    }

                    if (_window.Count >= windowLimit)
                    {
                        delay = TimeSpan.FromSeconds(30) - (now - _window.Peek()) + TimeSpan.FromMilliseconds(50);
                    }
                    else if (_window.Count > 0)
                    {
                        var minGap = TimeSpan.FromSeconds(1.0 / rps);
                        var sinceLast = now - _window.Last();
                        if (sinceLast < minGap)
                        {
                            delay = minGap - sinceLast;
                        }
                        else
                        {
                            _window.Enqueue(now);
                            return;
                        }
                    }
                    else
                    {
                        _window.Enqueue(now);
                        return;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            var wait = delay.GetValueOrDefault();
            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
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
