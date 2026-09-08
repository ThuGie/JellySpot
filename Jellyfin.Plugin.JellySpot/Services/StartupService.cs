using Jellyfin.Plugin.JellySpot.Helpers;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services;

public class StartupService : IScheduledTask
{
    private readonly ILogger<Plugin> _logger;
    private readonly LibraryMatchService _libraryMatch;

    public StartupService(ILogger<Plugin> logger, LibraryMatchService libraryMatch)
    {
        _logger = logger;
        _libraryMatch = libraryMatch;
    }

    public string Name => "JellySpot Startup";

    public string Key => "Jellyfin.Plugin.JellySpot.Startup";

    public string Description => "Registers file transformations for JellySpot";

    public string Category => "Startup Services";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("JellySpot • registering file transformations");

        for (int attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FileTransformationHelper.TryRegister(_logger))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        _libraryMatch.Warmup();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "JellySpot library match warmup failed");
                    }
                });
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("JellySpot • File Transformation plugin not found. Home tabs will not appear");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}
