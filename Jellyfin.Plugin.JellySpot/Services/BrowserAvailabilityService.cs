using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellySpot.Services;

/// <summary>Matches only the viewing user's visible audio, and invalidates on library/policy changes.</summary>
public sealed class BrowserAvailabilityService : IDisposable
{
    private readonly JellySpotStore _store;
    private readonly ILibraryManager _libraries;
    private readonly IUserManager _users;
    private readonly LibraryStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _revision;
    private readonly ConcurrentDictionary<Guid, (long Revision, DateTime Built, List<Audio> Audio)> _catalogs = new();
    private readonly ConcurrentDictionary<string, (long Revision, DateTime Built, Dictionary<string, string> Matches)> _matches = new();
    public BrowserAvailabilityService(JellySpotStore store, ILibraryManager libraries, IUserManager users, LibraryStorage storage)
    {
        _store = store; _libraries = libraries; _users = users; _storage = storage;
        _libraries.ItemAdded += LibraryChanged; _libraries.ItemUpdated += LibraryChanged; _libraries.ItemRemoved += LibraryChanged;
        _users.OnUserUpdated += UserChanged;
    }
    private void LibraryChanged(object? sender, ItemChangeEventArgs args) => Invalidate();
    private void UserChanged(object? sender, Jellyfin.Data.Events.GenericEventArgs<Jellyfin.Database.Implementations.Entities.User> args) => Invalidate();
    public void Invalidate() { Interlocked.Increment(ref _revision); _catalogs.Clear(); _matches.Clear(); }
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(Guid userId, string scope, string contentRevision, CancellationToken ct = default)
    {
        var user = _users.GetUserById(userId) ?? throw new UnauthorizedAccessException();
        var key = $"{userId:N}:{scope}:{contentRevision}"; var revision = Interlocked.Read(ref _revision);
        if (_matches.TryGetValue(key, out var hit) && hit.Revision == revision && DateTime.UtcNow - hit.Built < TimeSpan.FromMinutes(5)) return hit.Matches;
        await _gate.WaitAsync(ct);
        try
        {
            if (_matches.TryGetValue(key, out hit) && hit.Revision == revision && DateTime.UtcNow - hit.Built < TimeSpan.FromMinutes(5)) return hit.Matches;
            if (!_catalogs.TryGetValue(userId, out var catalog) || catalog.Revision != revision || DateTime.UtcNow - catalog.Built > TimeSpan.FromMinutes(5))
            {
                var audio = _libraries.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Audio], Recursive = true, IsVirtualItem = false }).OfType<Audio>().ToList();
                catalog = (revision, DateTime.UtcNow, audio); _catalogs[userId] = catalog;
            }
            var entries = await _store.ScopeTracksAsync(scope, ct); var indexes = await _store.TrackIndexesAsync(entries.Select(e => e.Track.Id), ct);
            var paths = catalog.Audio.Where(a => !string.IsNullOrEmpty(a.Path)).GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var titles = catalog.Audio.GroupBy(a => LibraryMatchService.NormalizeTitle(a.Name ?? "")).ToDictionary(g => g.Key, g => g.ToList());
            var matches = new Dictionary<string, string>();
            foreach (var entry in entries.DistinctBy(e => e.Track.Id))
            {
                ct.ThrowIfCancellationRequested(); var track = entry.Track; Audio? item = null;
                if (indexes.TryGetValue(track.Id, out var index) && !string.IsNullOrEmpty(index.RelativePath)) paths.TryGetValue(_storage.GetAbsolutePath(index.RelativePath), out item);
                if (item == null && titles.TryGetValue(LibraryMatchService.NormalizeTitle(track.Name), out var candidates)) item = LibraryMatchService.FindVisibleAudio(track, candidates);
                if (item != null) matches[track.Id] = item.Id.ToString("N");
            }
            _matches[key] = (revision, DateTime.UtcNow, matches);
            if (_matches.Count > 200) _matches.Clear();
            return matches;
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { _libraries.ItemAdded -= LibraryChanged; _libraries.ItemUpdated -= LibraryChanged; _libraries.ItemRemoved -= LibraryChanged; _users.OnUserUpdated -= UserChanged; _gate.Dispose(); }
}
