using Jellyfin.Plugin.JellySpot.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services;

public class FileTransformationHostedService : IHostedService
{
    private readonly ILogger<Plugin> _logger;

    public FileTransformationHostedService(ILogger<Plugin> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (FileTransformationHelper.TryRegister(_logger))
            {
                return;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("JellySpot • File Transformation plugin not found after startup retries");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
