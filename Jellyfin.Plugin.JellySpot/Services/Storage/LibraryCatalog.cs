using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySpot.Services.Storage;

public static class LibraryCatalog
{
    public sealed record LibraryInfo(string Id, string Name, string CollectionType, IReadOnlyList<string> Locations);

    public static List<LibraryInfo> ListLibraries(ILibraryManager libraryManager, ILogger? logger = null)
    {
        var byId = new Dictionary<string, LibraryInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var folder in libraryManager.GetVirtualFolders(true))
            {
                if (string.IsNullOrWhiteSpace(folder.ItemId) || !Guid.TryParse(folder.ItemId, out var guid))
                {
                    continue;
                }

                var id = guid.ToString("N");
                byId[id] = new LibraryInfo(
                    id,
                    folder.Name ?? id,
                    folder.CollectionType?.ToString() ?? string.Empty,
                    folder.Locations ?? []);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "JellySpot GetVirtualFolders failed");
        }

        try
        {
            foreach (var child in libraryManager.GetUserRootFolder().Children)
            {
                if (child is not CollectionFolder folder)
                {
                    continue;
                }

                var id = folder.Id.ToString("N");
                if (byId.ContainsKey(id) && byId[id].Locations.Count > 0)
                {
                    continue;
                }

                IReadOnlyList<string> locations = folder.PhysicalLocations ?? [];
                if (locations.Count == 0 && !string.IsNullOrWhiteSpace(folder.Path))
                {
                    locations = [folder.Path];
                }

                byId[id] = new LibraryInfo(
                    id,
                    folder.Name,
                    folder.CollectionType?.ToString() ?? string.Empty,
                    locations);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "JellySpot GetUserRootFolder failed");
        }

        return byId.Values
            .OrderBy(l => IsMusic(l.CollectionType) ? 0 : 1)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? ResolveStorageRoot(ILibraryManager libraryManager, ILogger? logger = null)
    {
        var config = Plugin.Instance?.Configuration;
        if (!string.IsNullOrWhiteSpace(config?.StorageRootPath))
        {
            return config.StorageRootPath.Trim();
        }

        var libraries = ListLibraries(libraryManager, logger);
        if (!string.IsNullOrWhiteSpace(config?.SelectedLibraryId))
        {
            var selected = libraries.FirstOrDefault(l =>
                string.Equals(l.Id, NormalizeId(config.SelectedLibraryId), StringComparison.OrdinalIgnoreCase));
            var selectedPath = selected?.Locations.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                return selectedPath;
            }
        }

        return libraries
            .Where(l => IsMusic(l.CollectionType))
            .SelectMany(l => l.Locations)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    public static bool IsMusic(string? collectionType)
    {
        return string.Equals(collectionType, "music", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeId(string? id)
    {
        return (id ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}
