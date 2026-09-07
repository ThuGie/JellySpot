using System.Runtime.Loader;
using Jellyfin.Plugin.JellySpot.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.JellySpot.ScheduledTasks;

public class StartupService : IScheduledTask
{
    private static readonly Guid IndexHtmlTransformationId = Guid.Parse("b8e4f2a1-6c3d-4f9a-9e7b-2a1c0d5e8f90");

    private readonly ILogger<StartupService> _logger;

    public StartupService(ILogger<StartupService> logger)
    {
        _logger = logger;
    }

    public string Name => "JellySpot Startup";

    public string Key => "Jellyfin.Plugin.JellySpot.Startup";

    public string Description => "Registers file transformations for JellySpot navigation";

    public string Category => "Startup Services";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("JellySpot registering file transformations");

        var fileTransformationAssembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x =>
                (x.GetName().Name ?? x.FullName ?? string.Empty)
                    .Contains("FileTransformation", StringComparison.OrdinalIgnoreCase));

        if (fileTransformationAssembly == null)
        {
            _logger.LogWarning(
                "File Transformation plugin not found. Dashboard → Plugins → JellySpot still works; home-nav shortcut will not.");
            return Task.CompletedTask;
        }

        Type? pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterfaceType == null)
        {
            _logger.LogWarning("File Transformation PluginInterface type not found");
            return Task.CompletedTask;
        }

        var payload = new JObject
        {
            ["id"] = IndexHtmlTransformationId,
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = GetType().Assembly.FullName,
            ["callbackClass"] = typeof(TransformationPatches).FullName,
            ["callbackMethod"] = nameof(TransformationPatches.IndexHtml)
        };

        pluginInterfaceType.GetMethod("RegisterTransformation")?.Invoke(null, new object?[] { payload });
        _logger.LogInformation("JellySpot registered index.html transformation");
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}
