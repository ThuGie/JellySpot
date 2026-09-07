using System.Runtime.Loader;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.JellySpot.Helpers;

public static class FileTransformationHelper
{
    private static readonly Guid IndexHtmlTransformationId = Guid.Parse("b8e4f2a1-6c3d-4f9a-9e7b-2a1c0d5e8f90");

    public static bool IsPresent(IApplicationPaths? applicationPaths = null)
    {
        if (AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .Any(x =>
            {
                string name = x.GetName().Name ?? x.FullName ?? string.Empty;
                return name.Contains("FileTransformation", StringComparison.OrdinalIgnoreCase);
            }))
        {
            return true;
        }

        try
        {
            string? pluginsPath = applicationPaths?.PluginsPath;
            if (!string.IsNullOrEmpty(pluginsPath) &&
                Directory.Exists(pluginsPath) &&
                Directory.EnumerateFileSystemEntries(pluginsPath)
                    .Any(path => Path.GetFileName(path).Contains("FileTransformation", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            // probe is best-effort
        }

        return false;
    }

    public static bool TryRegister(ILogger logger)
    {
        var fileTransformationAssembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x =>
                (x.GetName().Name ?? x.FullName ?? string.Empty)
                    .Contains("FileTransformation", StringComparison.OrdinalIgnoreCase));

        if (fileTransformationAssembly == null)
        {
            return false;
        }

        Type? pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterfaceType == null)
        {
            logger.LogWarning("JellySpot • File Transformation PluginInterface type not found");
            return false;
        }

        var payload = new JObject
        {
            ["id"] = IndexHtmlTransformationId,
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = typeof(TransformationPatches).Assembly.FullName,
            ["callbackClass"] = typeof(TransformationPatches).FullName,
            ["callbackMethod"] = nameof(TransformationPatches.IndexHtml)
        };

        pluginInterfaceType.GetMethod("RegisterTransformation")?.Invoke(null, new object?[] { payload });
        logger.LogInformation("JellySpot • registered index.html transformation");
        return true;
    }
}
